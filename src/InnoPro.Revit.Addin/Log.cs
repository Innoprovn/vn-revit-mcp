namespace InnoPro.Revit.Addin;

internal static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(Paths.LogDir);
            var file = Path.Combine(Paths.LogDir, $"addin-{DateTime.Now:yyyyMMdd}.log");
            lock (Gate)
                File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Ghi log không bao giờ được làm hỏng Revit.
        }
    }
}
