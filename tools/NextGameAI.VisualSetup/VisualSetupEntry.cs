using NextGameAI.Web.VisualDevelopment;

namespace NextGameAI.VisualSetup;

// Only deliberately constructed messages are displayed; raw exception messages are never printed.
internal sealed class OperationalFailure(string message) : InvalidOperationException(message);

internal static class VisualSetupEntry
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            return await ExecuteAsync(args, Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"),
                Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
                () => new VisualDatabaseGuard(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
                Console.Out, Console.Error, cancellation.Token);
        }
        finally { Console.CancelKeyPress -= handler; }
    }

    // Same dispatcher as Main; tests resolve only their temporary root, after intent validation.
    internal static async Task<int> ExecuteAsync(string[] args, string? dotnet, string? aspnet,
        Func<VisualDatabaseGuard> guardFactory, TextWriter output, TextWriter error, CancellationToken ct = default,
        Func<string, long>? availableSpace = null)
    {
        try
        {
            await VisualDatabasePreparer.RunOperationAsync(args, dotnet, aspnet, guardFactory, output, ct, availableSpace);
            await output.WriteLineAsync(args[0] == "check-synthetic"
                ? "Pré-checagem concluída. Nenhum arquivo foi criado ou alterado."
                : "Preparação sintética concluída e validada.");
            return 0;
        }
        catch (OperationCanceledException)
        { await error.WriteLineAsync("Operação cancelada. Resíduos exigem revisão explícita; ausência de mensagem não prova ausência de preparação."); return 2; }
        catch (OperationalFailure failure)
        { await error.WriteLineAsync(failure.Message); return 1; }
        catch (UnauthorizedAccessException)
        { await error.WriteLineAsync("Permissão insuficiente para inspecionar ou preparar o destino. Não desative as guardas."); return 1; }
        catch (IOException)
        { await error.WriteLineAsync("Falha de E/S: metadados, espaço, conflito ou publicação não puderam ser confirmados. Resultado indeterminado; preserve os artefatos para inspeção."); return 1; }
        catch (Exception)
        { await error.WriteLineAsync("Destino, armazenamento ou verificação recusados. Consulte a última etapa exibida. Preserve os artefatos; a ausência de confirmação não prova que a publicação falhou."); return 1; }
    }
}
