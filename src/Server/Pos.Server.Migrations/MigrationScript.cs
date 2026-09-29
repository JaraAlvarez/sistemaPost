using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Pos.Server.Migrations;

public enum MigrationKind
{
    /// <summary><c>V{version}__{module}__{description}.sql</c>: se ejecuta una sola vez, en orden de versión.</summary>
    Versioned,

    /// <summary><c>R__{module}__{description}.sql</c>: datos idempotentes; se reejecuta cuando cambia su checksum.</summary>
    Repeatable,

    /// <summary><c>A__{module}__{description}.sql</c>: se ejecuta al final de cada migración (p. ej. privilegios).</summary>
    Always,
}

/// <summary>Script de migración incrustado en el ensamblado.</summary>
public sealed partial record MigrationScript(
    string Name,
    MigrationKind Kind,
    string? Version,
    string Module,
    string Description,
    string Sql,
    string Checksum)
{
    /// <summary>Crea el script a partir del nombre de archivo. El checksum ignora BOM y finales de línea CRLF.</summary>
    public static MigrationScript Parse(string fileName, string content)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(content);

        var match = NamePattern().Match(fileName);
        if (!match.Success)
        {
            throw new FormatException(
                $"Nombre de script inválido '{fileName}'. Formatos: V2026.10.001__modulo__descripcion.sql, " +
                "R__modulo__descripcion.sql o A__modulo__descripcion.sql.");
        }

        var kind = match.Groups["prefix"].Value switch
        {
            "V" => MigrationKind.Versioned,
            "R" => MigrationKind.Repeatable,
            _ => MigrationKind.Always,
        };
        var version = match.Groups["version"].Success ? match.Groups["version"].Value : null;
        if ((kind == MigrationKind.Versioned) != (version is not null))
        {
            throw new FormatException($"Solo los scripts versionados (V) llevan versión: '{fileName}'.");
        }

        var normalized = content.TrimStart('﻿').ReplaceLineEndings("\n");
        return new MigrationScript(
            fileName,
            kind,
            version,
            match.Groups["module"].Value,
            match.Groups["description"].Value.Replace('_', ' '),
            normalized,
            ComputeChecksum(normalized));
    }

    public static string ComputeChecksum(string normalizedSql) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedSql)));

    /// <summary>Compara versiones <c>AAAA.MM.NNN</c> numéricamente.</summary>
    public static int CompareVersions(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var l = left.Split('.').Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        var r = right.Split('.').Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        for (var i = 0; i < Math.Min(l.Length, r.Length); i++)
        {
            var comparison = l[i].CompareTo(r[i]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return l.Length.CompareTo(r.Length);
    }

    [GeneratedRegex(
        @"^(?<prefix>[VRA])(?<version>\d{4}\.\d{2}\.\d{3})?__(?<module>[a-z]+)__(?<description>[a-z0-9_]+)\.sql$",
        RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
