using Microsoft.AspNetCore.Http;
using Pos.SharedKernel.Results;

namespace Pos.Api.Abstractions;

/// <summary>
/// Lectura del archivo de un formulario multipart DENTRO del handler. Si el archivo fuera un parámetro del endpoint, el
/// enrutador rechazaría con 415 las peticiones que no son multipart antes de que se verifique la sesión y el permiso.
/// </summary>
public static class UploadedFile
{
    public const string FieldName = "file";

    public static async Task<IFormFile?> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.HasFormContentType)
        {
            return null;
        }

        var form = await request.ReadFormAsync(cancellationToken);
        return form.Files.GetFile(FieldName) ?? (form.Files.Count > 0 ? form.Files[0] : null);
    }

    public static Error Missing() =>
        Error.Validation("FILES.MISSING", "Envíe el archivo como multipart/form-data en el campo 'file' (.xlsx o .csv).");
}
