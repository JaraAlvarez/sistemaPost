using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Pos.Terminal.Agent.Transports;

/// <summary>
/// Impresora instalada en Windows (con su controlador, p. ej. el de Epson o "Genérico / Solo texto"): el trabajo se envía en modo
/// <c>RAW</c> con OpenPrinter / StartDocPrinter / WritePrinter, así el controlador no reinterpreta los comandos ESC/POS.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSpoolerTransport(string printerName) : IPrinterTransport
{
    public const string DocumentName = "Tiquete POS";

    public Task<string> SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        Task.Run(() => Send(data.ToArray()), cancellationToken);

    /// <summary>Si Windows conoce una impresora con ese nombre (no imprime nada).</summary>
    public bool Exists()
    {
        if (!NativeMethods.OpenPrinter(printerName, out var handle, IntPtr.Zero))
        {
            return false;
        }

        handle.Dispose();
        return true;
    }

    private string Send(byte[] bytes)
    {
        if (!NativeMethods.OpenPrinter(printerName, out var handle, IntPtr.Zero))
        {
            throw new IOException($"Windows no encuentra la impresora \"{printerName}\".", new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        using (handle)
        {
            var documentName = Marshal.StringToHGlobalUni(DocumentName);
            var dataType = Marshal.StringToHGlobalUni("RAW");
            try
            {
                var info = new NativeMethods.DocInfo1 { DocumentName = documentName, OutputFile = IntPtr.Zero, DataType = dataType };
                if (NativeMethods.StartDocPrinter(handle, 1, in info) == 0)
                {
                    throw Failure("iniciar el trabajo");
                }

                try
                {
                    if (!NativeMethods.StartPagePrinter(handle))
                    {
                        throw Failure("iniciar la página");
                    }

                    if (!NativeMethods.WritePrinter(handle, bytes, bytes.Length, out var written) || written != bytes.Length)
                    {
                        throw Failure("enviar los datos");
                    }

                    NativeMethods.EndPagePrinter(handle);
                }
                finally
                {
                    NativeMethods.EndDocPrinter(handle);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(documentName);
                Marshal.FreeHGlobal(dataType);
            }
        }

        return $"Impresora de Windows \"{printerName}\"";
    }

    private IOException Failure(string step) =>
        new($"La impresora \"{printerName}\" no permitió {step}.", new Win32Exception(Marshal.GetLastPInvokeError()));
}

/// <summary>Funciones de winspool.drv (API del spooler de impresión de Windows).</summary>
[SupportedOSPlatform("windows")]
internal static partial class NativeMethods
{
    private const string WinSpool = "winspool.drv";

    [StructLayout(LayoutKind.Sequential)]
    internal struct DocInfo1
    {
        public IntPtr DocumentName;
        public IntPtr OutputFile;
        public IntPtr DataType;
    }

    [LibraryImport(WinSpool, EntryPoint = "OpenPrinterW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenPrinter(string printerName, out SafePrinterHandle printer, IntPtr defaults);

    [LibraryImport(WinSpool, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ClosePrinter(IntPtr printer);

    [LibraryImport(WinSpool, EntryPoint = "StartDocPrinterW", SetLastError = true)]
    internal static partial int StartDocPrinter(SafePrinterHandle printer, int level, in DocInfo1 docInfo);

    [LibraryImport(WinSpool, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EndDocPrinter(SafePrinterHandle printer);

    [LibraryImport(WinSpool, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool StartPagePrinter(SafePrinterHandle printer);

    [LibraryImport(WinSpool, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EndPagePrinter(SafePrinterHandle printer);

    [LibraryImport(WinSpool, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WritePrinter(SafePrinterHandle printer, byte[] bytes, int count, out int written);
}

/// <summary>Manejador de impresora que se cierra con ClosePrinter.</summary>
[SupportedOSPlatform("windows")]
internal sealed class SafePrinterHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafePrinterHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => NativeMethods.ClosePrinter(handle);
}
