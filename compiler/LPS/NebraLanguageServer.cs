using Nebra.LPS.Handlers;
using Microsoft.Extensions.DependencyInjection;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Server;

namespace Nebra.LPS;

public static class NebraLanguageServer
{
    public static async Task RunAsync()
    {
        var server = await LanguageServer.From(options =>
        {
            Configure(options, new NebraWorkspace());
            options
                .WithInput(Console.OpenStandardInput())
                .WithOutput(Console.OpenStandardOutput());
        }).ConfigureAwait(false);

        await server.WaitForExit;
    }

    /// <summary>
    /// Registers every handler and the workspace on <paramref name="options"/>, leaving the
    /// transport to the caller so the same server can run over stdio or over in-memory pipes.
    /// </summary>
    public static LanguageServerOptions Configure(LanguageServerOptions options, NebraWorkspace workspace)
    {
        return options
            .WithServices(services =>
            {
                services.AddSingleton(workspace);
            })
            .WithHandler<TextDocumentSyncHandler>()
            .WithHandler<HoverHandler>()
            .WithHandler<DefinitionHandler>()
            .WithHandler<CompletionHandler>()
            .WithHandler<DocumentSymbolHandler>()
            .WithHandler<SemanticTokensHandler>()
            .WithHandler<ReferencesHandler>()
            .WithHandler<RenameHandler>()
            .WithHandler<SignatureHelpHandler>()
            .WithHandler<CodeActionHandler>()
            .WithHandler<ExecuteCompileCommandHandler>()
            .OnInitialize((server, request, ct) =>
            {
                workspace.Initialize(request.RootUri?.GetFileSystemPath());
                workspace.SetServer(server);
                return Task.CompletedTask;
            });
    }
}
