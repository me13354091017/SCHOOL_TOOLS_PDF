using System.Diagnostics;
using System.IO;

namespace SchoolPdf.Services;

public static class AppDataPaths
{
    private static readonly string LegacyDataDirectory = Path.Combine(AppContext.BaseDirectory, "Data");
    private static readonly string LocalAppDataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string DataDirectory { get; }

    static AppDataPaths()
    {
        DataDirectory = ResolveDataDirectory();
    }

    private static string ResolveDataDirectory()
    {
        if (string.IsNullOrWhiteSpace(LocalAppDataDirectory))
            return LegacyDataDirectory;

        string appDataRoot = Path.Combine(LocalAppDataDirectory, "SCHOOL_TOOLS_PDF");
        string targetDataDirectory = Path.Combine(appDataRoot, "Data");
        if (string.Equals(Path.GetFullPath(LegacyDataDirectory), Path.GetFullPath(targetDataDirectory),
                StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(LegacyDataDirectory))
            return targetDataDirectory;

        string migrationMarker = Path.Combine(appDataRoot, ".legacy-data-migrated");
        using var migrationMutex = new Mutex(false, @"Local\SCHOOL_TOOLS_PDF_DataMigration");
        bool ownsMutex = false;
        try
        {
            try
            {
                ownsMutex = migrationMutex.WaitOne();
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }

            if (!ownsMutex)
                return LegacyDataDirectory;
            if (File.Exists(migrationMarker))
                return targetDataDirectory;

            Directory.CreateDirectory(targetDataDirectory);
            foreach (var sourceFile in Directory.EnumerateFiles(LegacyDataDirectory, "*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(LegacyDataDirectory, sourceFile);
                string destination = Path.Combine(targetDataDirectory, relativePath);
                if (File.Exists(destination))
                    continue;

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                string temporary = destination + ".migration-" + Guid.NewGuid().ToString("N");
                try
                {
                    File.Copy(sourceFile, temporary);
                    File.Move(temporary, destination);
                }
                finally
                {
                    try
                    {
                        if (File.Exists(temporary))
                            File.Delete(temporary);
                    }
                    catch { }
                }
            }

            File.WriteAllText(migrationMarker, DateTime.UtcNow.ToString("O"));
            return targetDataDirectory;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SCHOOL_TOOLS_PDF data migration failed; using legacy path: {ex}");
            return LegacyDataDirectory;
        }
        finally
        {
            if (ownsMutex)
                migrationMutex.ReleaseMutex();
        }
    }
}