using System.Runtime.InteropServices;

namespace OnaBages.Engine;

/// <summary>
/// Envia fitxers a la paperera de Windows (SHFileOperation, de shell32: ve amb Windows).
/// Sense cap finestra ni pregunta: la confirmació ja l'ha demanat la interfície.
/// </summary>
/// <remarks>
/// En una unitat de xarxa o un disc extraïble sense paperera, Windows l'esborra del tot.
/// </remarks>
public static class RecycleBin
{
    public static void Send(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("El fitxer no existeix.", path);

        var operation = new ShFileOpStruct
        {
            wFunc = FO_DELETE,
            pFrom = Path.GetFullPath(path) + "\0\0", // la llista de fitxers acaba amb dos zeros
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT,
        };
        int result = SHFileOperation(ref operation);
        if (result != 0 || operation.fAnyOperationsAborted)
            throw new IOException($"Windows no l'ha pogut enviar a la paperera (codi {result}).");
        if (File.Exists(path))
            throw new IOException("Windows no l'ha pogut enviar a la paperera (potser està obert en un altre programa).");
    }

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct lpFileOp);
}
