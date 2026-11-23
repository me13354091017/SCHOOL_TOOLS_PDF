using Microsoft.Data.Sqlite;

namespace SchoolPdf.Services;

/// <summary>启动后台预热：把首次打开 PDF、首次读写墨迹库时的一次性原生库加载与初始化提到启动后的空闲线程，
/// 用户真正点开文档时少等那一截。预热失败只是没享受到收益，不影响任何功能。</summary>
internal static class StartupPrewarm
{
    private static int _started;

    /// <summary>启动预热（幂等，由 App.OnStartup 调用；内部只排队，不阻塞启动路径）。</summary>
    public static void Run()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;
        _ = Task.Run(() =>
        {
            try { PrewarmPdfium(); }
            catch (Exception ex) { App.Log("预热.PDFium", ex); }
            try { PrewarmSqlite(); }
            catch (Exception ex) { App.Log("预热.SQLite", ex); }
        });
    }

    /// <summary>加载 pdfium.dll 并初始化 Docnet/PDFium 全局状态（首次 OpenDocument 时省掉这笔一次性开销）。</summary>
    private static void PrewarmPdfium() => _ = Docnet.Core.DocLib.Instance;

    /// <summary>加载 e_sqlite3 原生库并跑一遍连接初始化（内存库，不留任何文件）。</summary>
    private static void PrewarmSqlite()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        command.ExecuteScalar();
    }
}
