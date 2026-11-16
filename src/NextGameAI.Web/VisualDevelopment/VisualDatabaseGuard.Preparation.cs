using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace NextGameAI.Web.VisualDevelopment;

public sealed partial class VisualDatabaseGuard
{
    internal enum PublicationPoint { CreatePending, Write, Flush, Move, Validate }

    // Test-only observation points; never configured by command line or production configuration.
    internal Action<PublicationPoint>? PublicationProbe { get; set; }
    internal Action<PublicationPoint, FileStream>? PendingStreamProbe { get; set; }

    internal (bool MissingDirectories, string[] Conflicts, long AvailableBytes) CheckPreparation(
        Func<string, long>? availableSpace = null)
    {
        RequireFixedStorage();
        var directory = Path.GetDirectoryName(DatabasePath)!;
        var applicationDirectory = Path.GetDirectoryName(directory)!;
        RejectReparseComponents(Path.GetDirectoryName(applicationDirectory)!);
        foreach (var child in new[] { applicationDirectory, directory }) ValidateExistingDirectory(child);
        var conflicts = new List<string>();
        foreach (var suffix in PreparationSuffixes)
        {
            var path = DatabasePath + suffix;
            try { _ = File.GetAttributes(path); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            RejectReparseComponents(path);
            _ = FileIdentity(path);
            conflicts.Add(Path.GetFileName(path));
        }
        RequireFixedStorage();
        var bytes = (availableSpace ?? (root => new DriveInfo(root).AvailableFreeSpace))(Path.GetPathRoot(DatabasePath)!);
        if (bytes <= 0) throw new IOException("Espaço indisponível.");
        return (!Directory.Exists(directory), conflicts.ToArray(), bytes);
    }

    private static readonly string[] PreparationSuffixes = ["", ".ready", ".ready.pending", "-wal", "-shm", "-journal"];

    internal PreparationLease BeginPreparation()
    {
        RequireFixedStorage();
        var name = @"Local\NextGameAI.VisualSetup-" + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(DatabasePath.ToUpperInvariant())));
        var semaphore = new Semaphore(1, 1, name);
        if (!semaphore.WaitOne(0)) { semaphore.Dispose(); throw new InvalidOperationException("Já existe uma preparação em andamento."); }
        try
        {
            // Only two application-owned child directories may be created; the OS/test root must exist.
            var directory = Path.GetDirectoryName(DatabasePath)!;
            var applicationDirectory = Path.GetDirectoryName(directory)!;
            RejectReparseComponents(Path.GetDirectoryName(applicationDirectory)!);
            foreach (var child in new[] { applicationDirectory, directory }) ValidateExistingDirectory(child);
            foreach (var suffix in PreparationSuffixes) RequireAbsent(DatabasePath + suffix);
            foreach (var child in new[] { applicationDirectory, directory })
            {
                if (Directory.Exists(child)) RejectReparseComponents(child);
                else { RequireFixedStorage(); Directory.CreateDirectory(child); RejectReparseComponents(child); }
            }
            RequireFixedStorage(); RejectReparseComponents(directory);
            foreach (var suffix in PreparationSuffixes) RequireAbsent(DatabasePath + suffix);
            using (var created = new FileStream(DatabasePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                created.Flush(flushToDisk: true);
            return new PreparationLease(this, GetPreparationMarkerContents(), semaphore);
        }
        catch { semaphore.Release(); semaphore.Dispose(); throw; }
    }

    private static void ValidateExistingDirectory(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0) throw Rejected();
        RejectReparseComponents(path);
    }

    private static void RequireAbsent(string path)
    {
        try { _ = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        throw new InvalidOperationException("Destino visual já contém arquivos. Reexecução e sobrescrita não são permitidas.");
    }

    internal sealed class PreparationLease(VisualDatabaseGuard guard, string markerContents, Semaphore semaphore)
        : DbConnectionInterceptor, IDisposable
    {
        private bool disposed;
        internal void Check()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (guard.GetPreparationMarkerContents() != markerContents) throw Rejected();
            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            {
                var path = guard.DatabasePath + suffix;
                try { _ = File.GetAttributes(path); } catch (FileNotFoundException) { continue; }
                RejectReparseComponents(path); _ = FileIdentity(path);
            }
        }
        private void ValidateOpening(DbConnection connection)
        {
            if (connection is not SqliteConnection || connection.State != ConnectionState.Closed) throw Rejected();
            // EF SqliteDatabaseCreator.Exists clones this connection in ReadOnly mode.
            // Permit only that less-privileged probe of the same owned file; daily guard stays strict.
            var options = new SqliteConnectionStringBuilder(connection.ConnectionString);
            if (options.Mode == SqliteOpenMode.ReadOnly) options.Mode = SqliteOpenMode.ReadWrite;
            guard.ValidateConnectionString(options.ToString());
            Check(); RequireAbsent(guard.DatabasePath + ".ready");
        }
        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
        { ValidateOpening(connection); return result; }
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); ValidateOpening(connection); return ValueTask.FromResult(result); }

        internal void Publish(CancellationToken cancellationToken)
        {
            Check(); cancellationToken.ThrowIfCancellationRequested();
            var marker = guard.DatabasePath + ".ready";
            var pending = marker + ".pending";
            RequireAbsent(marker); RequireAbsent(pending);
            guard.PublicationProbe?.Invoke(PublicationPoint.CreatePending);
            using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                guard.PublicationProbe?.Invoke(PublicationPoint.Write);
                guard.PendingStreamProbe?.Invoke(PublicationPoint.Write, file);
                file.Write(Encoding.UTF8.GetBytes(markerContents));
                guard.PublicationProbe?.Invoke(PublicationPoint.Flush);
                guard.PendingStreamProbe?.Invoke(PublicationPoint.Flush, file);
                file.Flush(flushToDisk: true);
            }
            RejectReparseComponents(pending);
            var publishedIdentity = FileIdentity(pending);
            guard.PublicationProbe?.Invoke(PublicationPoint.Move);
            Check(); cancellationToken.ThrowIfCancellationRequested();
            File.Move(pending, marker, overwrite: false); // Commit point; do not report late cancellation as rollback.
            try
            {
                guard.PublicationProbe?.Invoke(PublicationPoint.Validate);
                guard.RequirePrepared();
            }
            catch
            {
                // Remove only the marker this operation just published, never an unknown replacement.
                RejectReparseComponents(marker);
                if (FileIdentity(marker) == publishedIdentity) File.Delete(marker);
                throw;
            }
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; semaphore.Release(); semaphore.Dispose();
        }
    }
}
