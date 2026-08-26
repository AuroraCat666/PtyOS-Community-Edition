namespace RPGReServer;

public static class Log
{
    private static readonly object _lock = new();

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        lock (_lock)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";
            Console.WriteLine(line);
            try
            {
                string dir = Path.Combine(AppContext.BaseDirectory, "logs");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "server.log"), line + Environment.NewLine);
            }
            catch { /* 日志写入失败不影响主流程 */ }
        }
    }
}
