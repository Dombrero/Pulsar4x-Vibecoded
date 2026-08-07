using System;
using NUnit.Framework;
using Pulsar4X.Api;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Api;
using Pulsar4X.Engine.Orders;

namespace Pulsar4X.Tests
{
    /// <summary>
    /// Base fixture for the engine side of the API layer: a fresh test universe per test, an
    /// <see cref="EngineGameServer"/> for the push-only <see cref="IGameServer"/> contract (connect,
    /// time/command writes, the event stream) and a <see cref="GameProjector"/> for the engine→DTO
    /// projection. The client-side adapter and galaxy model live in Pulsar4X.Client; these tests stay
    /// free of any UI dependency.
    /// </summary>
    public abstract class ApiTestBase
    {
        private protected Game _game = null;
        private protected IGameServer _server;
        private protected GameProjector _projector;

        [SetUp]
        public void SetUpApi()
        {
            _game = TestingUtilities.CreateTestUniverse(1, new DateTime(2050, 1, 1));
            // Make TimeStep run synchronously on this thread so post-step reads are deterministic.
            _game.Settings.EnforceSingleThread = true;
            _server = new EngineGameServer(_game);
            _projector = new GameProjector(_game);
        }

        [TearDown]
        public void TearDownApi()
        {
            // Release the server's engine hooks (time pulse subscription) between tests.
            (_server as IDisposable)?.Dispose();
        }

        private protected PlayerSession Connect()
        {
            var result = _server.Connect(new ConnectRequest { PlayerName = "Tester" });
            Assert.That(result.Success, Is.True, result.FailureReason);
            return result.Session;
        }

        // Projects the test universe's single system for the given session's faction.
        private protected SystemSnapshot ProjectSystem(PlayerSession session)
            => _projector.ProjectSystem(_game.Systems[0].ID, session.FactionId)!;

        /// <summary>Queue an engine order through the inbox (same as legacy <c>OrderHandler.HandleOrder</c> in tests).</summary>
        private protected bool QueueOrder(EntityCommand command)
            => OrderEnqueue.Enqueue(_game, command);

        /// <summary>Player issue — sets <see cref="OrderSource.Issued"/> and pauses standing.</summary>
        private protected bool IssuePlayerOrder(EntityCommand command)
            => OrderEnqueue.Issued(_game, command);
    }
}
