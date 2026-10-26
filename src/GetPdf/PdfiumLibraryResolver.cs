using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GetPdf;

/// <summary>
/// NAPS2.Pdfium.Binaries ships pdfium.dll in an architecture subfolder (_win64) and loads it
/// by full path itself; our own [DllImport("pdfium")] calls resolve to that same file, so both
/// share one loaded module (and its one-time FPDF_InitLibrary state).
/// </summary>
internal static class PdfiumLibraryResolver
{
    [ModuleInitializer]
    internal static void Register() =>
        NativeLibrary.SetDllImportResolver(typeof(PdfiumLibraryResolver).Assembly, Resolve);

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!name.Equals("pdfium", StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
        string folder = Environment.Is64BitProcess ? "_win64" : "_win32";
        foreach (string candidate in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, folder, "pdfium.dll"),
                     Path.Combine(AppContext.BaseDirectory, "pdfium.dll")
                 })
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out IntPtr handle))
                return handle;
        return IntPtr.Zero;
    }
}
