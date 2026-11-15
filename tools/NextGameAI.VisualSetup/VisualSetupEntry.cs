namespace NextGameAI.VisualSetup;

internal static class VisualSetupEntry
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        try
        {
            await VisualDatabasePreparer.RunAsync(args, cancellation.Token);
            Console.WriteLine("Preparação sintética concluída e validada."); return 0;
        }
        catch (OperationCanceledException)
        { Console.Error.WriteLine("Preparação cancelada. Nenhum sucesso foi declarado; resíduos exigem revisão explícita."); return 2; }
        catch (Exception)
        { Console.Error.WriteLine("Preparação recusada ou incompleta. Nenhum arquivo existente será reutilizado ou sobrescrito. Consulte a documentação."); return 1; }
    }
}
