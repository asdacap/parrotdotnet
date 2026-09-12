using Parrot.Protocol;

namespace Parrot.Cli.Tests;

internal sealed class QuestionInteractionPresenterTests
{
    [Test]
    public async Task Reconciliation_deduplicates_and_switching_filters_stale_requests(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        var invoker = new ScriptedInvoker();
        var pending = new PendingQuestion { Id = "question" };
        invoker.AddPendingQuestion("first", pending);
        var presenter = new QuestionInteractionPresenter(new Parrot.Protocol.Parrot.ParrotClient(invoker));
        var first = presenter.Attach("first");
        using var firstCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var firstRunning = presenter.Reconcile(first, firstCancellation.Token);
        try
        {
            _ = await presenter.WaitToRead(token);
            _ = await Assert.That((presenter.Read() ?? throw new InvalidOperationException("Expected a discovered question")).Pending.Id).IsEqualTo(pending.Id);
            var initialLists = invoker.PendingQuestionLists;
            while (invoker.PendingQuestionLists < initialLists + 2)
            {
                await Task.Delay(5, token);
            }

            presenter.Observe(first, pending);
            _ = await Assert.That(presenter.Read()).IsNull();
            presenter.Retry(first, pending);
            var second = presenter.Attach("second");
            await firstCancellation.CancelAsync();
            await firstRunning.WaitAsync(token);
            var stoppedLists = invoker.PendingQuestionLists;
            await Task.Delay(350, token);
            _ = await Assert.That(invoker.PendingQuestionLists).IsEqualTo(stoppedLists);
            presenter.Observe(first, new PendingQuestion { Id = "stale-observation" });
            presenter.Retry(first, pending);
            _ = await Assert.That(presenter.Read()).IsNull();

            invoker.AddPendingQuestion("second", pending);
            using var secondCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var secondRunning = presenter.Reconcile(second, secondCancellation.Token);
            try
            {
                _ = await presenter.WaitToRead(token);
                var discovered = presenter.Read() ?? throw new InvalidOperationException("Expected a discovered question");
                _ = await Assert.That(discovered.Session).IsEqualTo(second);
                _ = await Assert.That(discovered.Pending.Id).IsEqualTo(pending.Id);
                _ = await Assert.That(presenter.Read()).IsNull();
            }
            finally
            {
                await secondCancellation.CancelAsync();
                await secondRunning;
            }
        }
        finally
        {
            await firstCancellation.CancelAsync();
            await firstRunning;
        }
    }
}
