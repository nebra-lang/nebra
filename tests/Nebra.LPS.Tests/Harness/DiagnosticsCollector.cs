using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nebra.LPS.Tests.Harness;

/// <summary>
/// Receives <c>textDocument/publishDiagnostics</c> notifications and lets a test wait for the
/// next one published for a given document.
/// </summary>
public sealed class DiagnosticsCollector
{
    private readonly object _gate = new();
    private readonly List<(DocumentUri Uri, TaskCompletionSource<IReadOnlyList<Diagnostic>> Waiter)> _waiters = [];
    private readonly Dictionary<DocumentUri, IReadOnlyList<Diagnostic>> _latest = new();

    public void Record(PublishDiagnosticsParams published)
    {
        var diagnostics = published.Diagnostics.ToList();
        List<TaskCompletionSource<IReadOnlyList<Diagnostic>>> ready;

        lock (_gate)
        {
            _latest[published.Uri] = diagnostics;
            ready = _waiters.Where(w => w.Uri == published.Uri).Select(w => w.Waiter).ToList();
            _waiters.RemoveAll(w => w.Uri == published.Uri);
        }

        foreach (var waiter in ready)
        {
            waiter.TrySetResult(diagnostics);
        }
    }

    /// <summary>
    /// Returns a task that completes with the next diagnostics published for <paramref name="uri"/>.
    /// Register before triggering the change that publishes them, so the notification cannot be
    /// missed.
    /// </summary>
    public Task<IReadOnlyList<Diagnostic>> WaitForNext(DocumentUri uri)
    {
        var waiter = new TaskCompletionSource<IReadOnlyList<Diagnostic>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate)
        {
            _waiters.Add((uri, waiter));
        }

        return waiter.Task;
    }

    public IReadOnlyList<Diagnostic> Latest(DocumentUri uri)
    {
        lock (_gate)
        {
            return _latest.TryGetValue(uri, out var diagnostics) ? diagnostics : [];
        }
    }
}
