using System.Data.Common;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace NextGameAI.Web.VisualDevelopment;

// Windows-only, deliberately limited to this application's one visual database.
public sealed class VisualDatabaseGuard : DbConnectionInterceptor
{
    public const string FileName = "nextgameai.visual.db";
    public const string PreparationVersion = "NextGameAI.Visual/1/20261024220151_AddCatalogEnrichmentMetadata";
    public string DatabasePath { get; }
    public string ConnectionString { get; }
    private readonly Func<string, uint> classifyDrive;

    public VisualDatabaseGuard(string localApplicationData) : this(localApplicationData, GetDriveType) { }

    // Internal seam for synthetic drive classifications; normal callers always use the Windows query.
    internal VisualDatabaseGuard(string localApplicationData, Func<string, uint> classifyDrive)
    {
        this.classifyDrive = classifyDrive;
        if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(localApplicationData) ||
            localApplicationData.StartsWith(@"\\", StringComparison.Ordinal)) throw Rejected();
        DatabasePath = Path.GetFullPath(Path.Combine(localApplicationData, "NextGameAI", "VisualDevelopment", FileName));
        RequireFixedStorage();
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWrite, Cache = SqliteCacheMode.Private,
            ForeignKeys = true, Pooling = false
        }.ToString();
    }

    public void ValidateConnectionString(string value)
    {
        SqliteConnectionStringBuilder options;
        try { options = new(value); }
        catch (ArgumentException) { throw Rejected(); }
        var expected = new SqliteConnectionStringBuilder(ConnectionString);
        // Reject even harmless extra options: the effective connection has one supported shape.
        if (options.Count != expected.Count || options.Keys.Cast<string>().Any(k => !expected.ContainsKey(k)) ||
            options.Mode != SqliteOpenMode.ReadWrite || options.Cache != SqliteCacheMode.Private ||
            options.ForeignKeys != true || options.Pooling || !Path.IsPathFullyQualified(options.DataSource) ||
            options.DataSource.StartsWith(@"\\", StringComparison.Ordinal) ||
            !string.Equals(Path.GetFullPath(options.DataSource), DatabasePath, StringComparison.OrdinalIgnoreCase))
            throw Rejected();
        options.DataSource = DatabasePath;
        if (!string.Equals(options.ToString(), expected.ToString(), StringComparison.Ordinal)) throw Rejected();
        RequireFixedStorage();
    }

    private void RequireFixedStorage()
    {
        var root = Path.GetPathRoot(DatabasePath);
        if (string.IsNullOrEmpty(root)) throw Rejected();
        uint type;
        try { type = classifyDrive(root); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        { throw Rejected(); }
        // DRIVE_FIXED only. Unknown, unavailable, removable, remote, optical and RAM are refused.
        if (type != 3) throw Rejected();
    }

    // Metadata handles only; never opens SQLite, creates directories, migrates or prepares data.
    public void RequirePrepared()
    {
        try
        {
            RequireFixedStorage(); // Reject changed mappings before consulting any target file.
            RejectReparseComponents(DatabasePath);
            // Volume mount points and directory redirects are unsupported: the walk rejects all
            // reparse components, so the accepted path cannot cross into a mounted volume.
            RequireFixedStorage();
            var identity = FileIdentity(DatabasePath);
            var marker = DatabasePath + ".ready";
            RejectReparseComponents(marker);
            _ = FileIdentity(marker);
            if (new FileInfo(marker).Length > 512 || File.ReadAllText(marker) != PreparationVersion + "\n" + identity)
                throw Rejected();
            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            {
                var auxiliary = DatabasePath + suffix;
                // File.Exists follows links and may hide a dangling reparse point.
                try { _ = File.GetAttributes(auxiliary); }
                catch (FileNotFoundException) { continue; }
                RejectReparseComponents(auxiliary); _ = FileIdentity(auxiliary);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { throw Rejected(); }
    }

    private static void RejectReparseComponents(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            var attributes = File.GetAttributes(current); // Missing/indeterminate components fail closed.
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw Rejected();
        }
    }

    // Also usable by the future explicit preparer after completing migrations and synthetic data.
    // Binding the marker to file identity prevents reusing it for a replacement database.
    public string GetPreparationMarkerContents()
    {
        RequireFixedStorage();
        RejectReparseComponents(DatabasePath);
        RequireFixedStorage();
        return PreparationVersion + "\n" + FileIdentity(DatabasePath);
    }

    private static string FileIdentity(string path)
    {
        using var handle = CreateFile(path, 0x80 /* FILE_READ_ATTRIBUTES */, 7, IntPtr.Zero, 3 /* OPEN_EXISTING */,
            0x00200000 /* FILE_FLAG_OPEN_REPARSE_POINT */, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info) || info.NumberOfLinks != 1 ||
            (info.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw Rejected();
        return $"{info.VolumeSerialNumber:X8}:{info.FileIndexHigh:X8}:{info.FileIndexLow:X8}";
    }

    public void Validate(DbConnection connection)
    {
        if (connection is not SqliteConnection || connection.State != System.Data.ConnectionState.Closed) throw Rejected();
        ValidateConnectionString(connection.ConnectionString);
        RequirePrepared();
    }
    public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    { Validate(connection); return result; }
    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData,
        InterceptionResult result, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); Validate(connection); return ValueTask.FromResult(result); }

    private static InvalidOperationException Rejected() => new(
        "Ambiente visual indisponível: configuração ou destino recusado, ou banco ainda não preparado. A preparação pertence à etapa 4C.3A.2.");

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint VolumeSerialNumber, SizeHigh, SizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", EntryPoint = "GetDriveTypeW", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveType(string rootPath);
}
