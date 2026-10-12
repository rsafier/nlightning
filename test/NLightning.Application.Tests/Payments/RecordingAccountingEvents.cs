using System.Diagnostics.CodeAnalysis;

namespace NLightning.Application.Tests.Payments;

using Domain.Accounting.Interfaces;
using Domain.Accounting.Models;

/// <summary>
/// The accounting feed of a test node whose units of work are mocks (NL-602): each unit of work stages its events on
/// its own <see cref="Staged"/> view (<see cref="Begin"/>), and its save moves them to <see cref="Saved"/>
/// (<see cref="Staged.Commit"/>), so a test sees only what a save committed.
/// </summary>
/// <remarks>The repository is a Moq mock (only <c>Add</c> and <c>ExistsAsync</c> are set up), so members added to
/// <see cref="IAccountingEventDbRepository"/> later need no change here.</remarks>
[ExcludeFromCodeCoverage]
internal sealed class RecordingAccountingEvents
{
    private readonly Lock _sync = new();
    private readonly List<AccountingEventModel> _saved = [];

    /// <summary>The events committed so far, in save order.</summary>
    public IReadOnlyList<AccountingEventModel> Saved
    {
        get
        {
            lock (_sync)
                return _saved.ToList();
        }
    }

    /// <summary>A new unit of work's view of the feed.</summary>
    public Staged Begin() => new(this);

    internal sealed class Staged
    {
        private readonly RecordingAccountingEvents _owner;
        private readonly List<AccountingEventModel> _staged = [];

        public Staged(RecordingAccountingEvents owner)
        {
            _owner = owner;
            var repository = new Mock<IAccountingEventDbRepository>();
            repository.Setup(r => r.Add(It.IsAny<AccountingEventModel>()))
                      .Callback<AccountingEventModel>(e =>
                       {
                           lock (_owner._sync)
                               _staged.Add(e);
                       });
            repository.Setup(r => r.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync((string key, CancellationToken _) =>
                       {
                           lock (_owner._sync)
                               return _owner._saved.Concat(_staged).Any(e => e.EventKey == key);
                       });
            Repository = repository.Object;
        }

        /// <summary>The unit of work's <see cref="IAccountingEventDbRepository"/>.</summary>
        public IAccountingEventDbRepository Repository { get; }

        /// <summary>Moves the staged events to the saved ones (the unit of work's save).</summary>
        public void Commit()
        {
            lock (_owner._sync)
            {
                _owner._saved.AddRange(_staged);
                _staged.Clear();
            }
        }
    }
}