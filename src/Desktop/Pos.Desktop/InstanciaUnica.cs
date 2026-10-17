using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Pos.Desktop;

/// <summary>Si BusinessPost ya está abierto, la segunda apertura trae al frente la ventana existente (restaurándola si estaba minimizada).</summary>
internal static partial class InstanciaUnica
{
    private const int SwRestore = 9;

    public static void TraerAlFrente()
    {
        using var actual = Process.GetCurrentProcess();
        foreach (var otro in Process.GetProcessesByName(actual.ProcessName))
        {
            using (otro)
            {
                if (otro.Id == actual.Id || otro.SessionId != actual.SessionId)
                    continue;
                var ventana = otro.MainWindowHandle;
                if (ventana == IntPtr.Zero)
                    continue;
                // Solo se restaura si está minimizada (y con ShowWindowAsync): un ShowWindow directo desde este proceso usaría el modo de
                // inicio del acceso directo (ventana normal) y le quitaría el maximizado a la ventana abierta.
                if (IsIconic(ventana))
                    ShowWindowAsync(ventana, SwRestore);
                SetForegroundWindow(ventana);
                return;
            }
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr ventana);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindowAsync(IntPtr ventana, int comando);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(IntPtr ventana);
}
