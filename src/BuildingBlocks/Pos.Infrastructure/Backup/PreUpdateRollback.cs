using Dapper;
using Npgsql;

namespace Pos.Infrastructure.Backup;

/// <summary>Resultado de restaurar el backup PRE_UPDATE de una actualización fallida (IN4, IL1, IL2).</summary>
public sealed record PreUpdateRestoreOutcome(
    BackupHeader Header,
    string Database,
    bool SealMatches,
    IReadOnlyDictionary<string, long> Counts,
    bool CountsMatch,
    int PrivilegesCopied)
{
    public bool IsComplete => SealMatches && CountsMatch;
}

/// <summary>
/// Vuelta atrás de una actualización cuya migración falló a mitad (IN4): restaura el backup PRE_UPDATE de ESTE equipo en una BD NUEVA,
/// SIN migrarla (queda en el esquema de la versión anterior, la que vuelve a arrancar).
/// <list type="bullet">
/// <item>IL1: el volcado va sin dueños ni privilegios (<c>--no-owner --no-privileges</c>) y no se corren los scripts "always" de la versión
/// nueva (su <c>A__system__privileges</c> nombra objetos que el esquema anterior no tiene). Por eso se COPIAN los privilegios que tenía
/// la BD activa para los roles de la aplicación: son los que dejó la versión anterior (la migración fallida no llegó a sus scripts
/// "always"); los de objetos que no existen en la BD restaurada (los creó la migración fallida) se omiten.</item>
/// <item>IL2: la revisión posterior usa solo lo que el backup trae: el sello de auditoría (si el backup tenía uno) y los conteos de las
/// tablas del encabezado; no se usa el verificador de auditoría de la versión nueva, que espera su propio esquema.</item>
/// <item>IN4: antes de empezar se revisa el espacio libre (BD actual + 20 % en el disco de los datos; el doble del paquete en la carpeta de
/// trabajo) y quien llama pone un límite de tiempo (<see cref="DefaultTimeout"/>).</item>
/// </list>
/// </summary>
public sealed class PreUpdateRollback(IPgTools tools)
{
    /// <summary>Roles de la aplicación cuyos privilegios se copian.</summary>
    public static readonly string[] ApplicationRoles = ["pos_app", "pos_backup", "pos_migrator"];

    /// <summary>Límite razonable para restaurar una tienda (un backup local de varios GB tarda minutos).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(60);

    /// <param name="packageFile">Backup PRE_UPDATE (cifrado con la clave de este equipo).</param>
    /// <param name="keys">Clave local de los backups.</param>
    /// <param name="superuserConnectionString">Superusuario del clúster (cualquier BD).</param>
    /// <param name="sourceDatabase">BD activa (a medio migrar): de ella se copian los privilegios.</param>
    /// <param name="targetDatabase">BD nueva donde se restaura.</param>
    /// <param name="workDirectory">Carpeta temporal para descifrar el paquete.</param>
    /// <param name="dataDirectory">Carpeta de los datos de PostgreSQL (para revisar el espacio libre); <c>null</c> para no revisarlo.</param>
    /// <param name="createDatabase">Crea la BD vacía con los permisos de conexión estándar.</param>
    /// <param name="cancellationToken">Límite de tiempo de quien llama.</param>
    public async Task<PreUpdateRestoreOutcome> RestoreAsync(
        string packageFile,
        BackupKeyStore keys,
        string superuserConnectionString,
        string sourceDatabase,
        string targetDatabase,
        string workDirectory,
        string? dataDirectory,
        Func<string, string, CancellationToken, Task> createDatabase,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(createDatabase);
        BackupHeader header;
        await using (var input = File.OpenRead(packageFile))
        {
            header = await BackupPackage.ReadHeaderAsync(input, cancellationToken);
        }

        var key = keys.Exists ? keys.Read() : null;
        if (key is null || BackupKeyStore.KeyId(key) != header.KeyId)
        {
            throw new BackupPackageException("El backup previo no se puede abrir con la clave de este equipo.");
        }

        var source = Connection(superuserConnectionString, sourceDatabase);
        var target = Connection(superuserConnectionString, targetDatabase);
        await EnsureFreeSpaceAsync(source, sourceDatabase, packageFile, workDirectory, dataDirectory, cancellationToken);

        var (_, dump) = await BackupEngine.ExtractAsync(packageFile, key, workDirectory, cancellationToken);
        await createDatabase(superuserConnectionString, targetDatabase, cancellationToken);
        await tools.RestoreAsync(target, dump, BackupRestoreProcedure.SchemaOwnerRole, cancellationToken);
        var privileges = await CopyPrivilegesAsync(source, target, ApplicationRoles, cancellationToken);
        await GrantReportingAsync(target, cancellationToken);

        await using var connection = new NpgsqlConnection(target);
        await connection.OpenAsync(cancellationToken);
        var sealMatches = await SealMatchesAsync(connection, header, cancellationToken);
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in header.Counts.Keys)
        {
            // to_regclass devuelve el nombre ya citado por PostgreSQL, o null si la tabla no está (entonces no coincide, abajo).
            if (await connection.ExecuteScalarAsync<string?>(new CommandDefinition("SELECT to_regclass(@table)::text", new { table }, cancellationToken: cancellationToken)) is not { } quoted)
            {
                continue;
            }

#pragma warning disable CA2100 // Nombre de tabla comprobado y citado por PostgreSQL (to_regclass).
            counts[table] = await connection.ExecuteScalarAsync<long>(new CommandDefinition($"SELECT count(*) FROM {quoted}", cancellationToken: cancellationToken));
#pragma warning restore CA2100
        }

        var countsMatch = header.Counts.All(c => counts.TryGetValue(c.Key, out var value) && value == c.Value);
        return new PreUpdateRestoreOutcome(header, targetDatabase, sealMatches, counts, countsMatch, privileges);
    }

    /// <summary>
    /// Copia los privilegios EXPLÍCITOS de <paramref name="roles"/> (y la revocación a PUBLIC) de esquemas, tablas, vistas, secuencias y
    /// funciones de la BD <paramref name="sourceConnectionString"/> a la misma estructura de <paramref name="targetConnectionString"/>.
    /// Los objetos que no existen en el destino se omiten. Devuelve cuántas órdenes se aplicaron.
    /// </summary>
    public static async Task<int> CopyPrivilegesAsync(
        string sourceConnectionString, string targetConnectionString, IReadOnlyCollection<string> roles, CancellationToken cancellationToken)
    {
        List<string> statements;
        await using (var source = new NpgsqlConnection(sourceConnectionString))
        {
            await source.OpenAsync(cancellationToken);
            statements = [.. await source.QueryAsync<string>(new CommandDefinition(PrivilegesSql, new { roles = roles.ToArray() }, cancellationToken: cancellationToken))];
        }

        var applied = 0;
        await using var target = new NpgsqlConnection(targetConnectionString);
        await target.OpenAsync(cancellationToken);
        await using var transaction = await target.BeginTransactionAsync(cancellationToken);
        foreach (var statement in statements)
        {
            await target.ExecuteAsync(new CommandDefinition("SAVEPOINT privilegio", transaction: transaction, cancellationToken: cancellationToken));
            try
            {
#pragma warning disable CA2100 // Órdenes armadas por PostgreSQL con format('%I') a partir de su propio catálogo.
                await target.ExecuteAsync(new CommandDefinition(statement, transaction: transaction, cancellationToken: cancellationToken));
#pragma warning restore CA2100
                await target.ExecuteAsync(new CommandDefinition("RELEASE SAVEPOINT privilegio", transaction: transaction, cancellationToken: cancellationToken));
                applied++;
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedFunction
                or PostgresErrorCodes.InvalidSchemaName or PostgresErrorCodes.UndefinedObject or PostgresErrorCodes.WrongObjectType)
            {
                // El objeto lo creó la migración fallida: no existe en la BD restaurada.
                await target.ExecuteAsync(new CommandDefinition("ROLLBACK TO SAVEPOINT privilegio", transaction: transaction, cancellationToken: cancellationToken));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return applied;
    }

    /// <summary>
    /// IR2: respaldo para las vistas de <c>reporting</c>. Si la migración falló en un repetible posterior a <c>R__reporting__views</c> (que
    /// las borra y las vuelve a crear) o en los scripts "always", en la BD activa quedaron SIN privilegios y la copia tampoco los trae. Todas
    /// las versiones dan a pos_app solo lectura de ese esquema (A__system__privileges), así que se otorga eso. Lo exacto lo deja después el
    /// migrador de la versión anterior con <c>apply-always</c>, si lo tiene.
    /// </summary>
    public static async Task GrantReportingAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            DO $$
            BEGIN
                IF to_regnamespace('reporting') IS NOT NULL AND EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pos_app') THEN
                    GRANT USAGE ON SCHEMA reporting TO pos_app;
                    GRANT SELECT ON ALL TABLES IN SCHEMA reporting TO pos_app;
                END IF;
            END
            $$
            """,
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Órdenes GRANT/REVOKE armadas desde el catálogo (aclexplode). Para cada objeto con privilegios explícitos: primero se quita lo que
    /// PUBLIC recibe por defecto si en el origen no lo tiene (funciones y el esquema public), luego los privilegios de cada rol.
    /// </summary>
    private const string PrivilegesSql = """
        WITH schemas AS (
            SELECT n.oid, n.nspname, n.nspacl FROM pg_namespace n
            WHERE n.nspname NOT LIKE 'pg\_%' AND n.nspname <> 'information_schema'
        )
        SELECT format('REVOKE ALL ON SCHEMA %I FROM PUBLIC', s.nspname)
        FROM schemas s
        WHERE s.nspacl IS NOT NULL AND NOT EXISTS (SELECT 1 FROM aclexplode(s.nspacl) a WHERE a.grantee = 0)
        UNION ALL
        SELECT format('GRANT %s ON SCHEMA %I TO %I', a.privilege_type, s.nspname, r.rolname)
        FROM schemas s, aclexplode(s.nspacl) a JOIN pg_roles r ON r.oid = a.grantee
        WHERE r.rolname = ANY(@roles)
        UNION ALL
        SELECT format('GRANT %s ON %s %I.%I TO %I', a.privilege_type, CASE WHEN c.relkind = 'S' THEN 'SEQUENCE' ELSE 'TABLE' END,
            s.nspname, c.relname, r.rolname)
        FROM pg_class c JOIN schemas s ON s.oid = c.relnamespace, aclexplode(c.relacl) a JOIN pg_roles r ON r.oid = a.grantee
        WHERE c.relkind IN ('r', 'p', 'v', 'm', 'f', 'S') AND r.rolname = ANY(@roles)
        UNION ALL
        SELECT format('REVOKE ALL ON FUNCTION %s FROM PUBLIC', p.oid::regprocedure)
        FROM pg_proc p JOIN schemas s ON s.oid = p.pronamespace
        WHERE p.proacl IS NOT NULL AND NOT EXISTS (SELECT 1 FROM aclexplode(p.proacl) a WHERE a.grantee = 0)
        UNION ALL
        SELECT format('GRANT %s ON FUNCTION %s TO %I', a.privilege_type, p.oid::regprocedure, r.rolname)
        FROM pg_proc p JOIN schemas s ON s.oid = p.pronamespace, aclexplode(p.proacl) a JOIN pg_roles r ON r.oid = a.grantee
        WHERE r.rolname = ANY(@roles)
        """;

    private static async Task EnsureFreeSpaceAsync(
        string source, string sourceDatabase, string packageFile, string workDirectory, string? dataDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workDirectory);
        var package = new FileInfo(packageFile).Length;
        CheckDrive(workDirectory, package * 2, "la carpeta temporal");
        if (dataDirectory is null)
        {
            return;
        }

        await using var connection = new NpgsqlConnection(source);
        await connection.OpenAsync(cancellationToken);
        var size = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT pg_database_size(@sourceDatabase)", new { sourceDatabase }, cancellationToken: cancellationToken));
        CheckDrive(dataDirectory, size + (size / 5), "el disco de los datos");
    }

    private static void CheckDrive(string path, long required, string what)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root))
        {
            return;
        }

        var free = new DriveInfo(root).AvailableFreeSpace;
        if (free < required)
        {
            throw new IOException(
                $"No hay espacio suficiente en {what} ({root}) para restaurar el backup previo: hacen falta {required / 1048576} MB y hay {free / 1048576} MB libres.");
        }
    }

    private static async Task<bool> SealMatchesAsync(NpgsqlConnection connection, BackupHeader header, CancellationToken cancellationToken)
    {
        if (header.AuditSealNo is null)
        {
            return true;
        }

        if (await connection.ExecuteScalarAsync<string?>(new CommandDefinition("SELECT to_regclass('audit.audit_seals')::text", cancellationToken: cancellationToken)) is null)
        {
            return false;
        }

        var hash = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT seal_hash FROM audit.audit_seals WHERE node_id = @node AND seal_no = @no",
            new { node = header.NodeId, no = header.AuditSealNo }, cancellationToken: cancellationToken));
        return hash is not null && Pos.Infrastructure.Auditing.AuditHasher.ShortCode(hash) == header.AuditSealCode;
    }

    private static string Connection(string superuser, string database) =>
        new NpgsqlConnectionStringBuilder(superuser) { Database = database, Pooling = false }.ConnectionString;
}
