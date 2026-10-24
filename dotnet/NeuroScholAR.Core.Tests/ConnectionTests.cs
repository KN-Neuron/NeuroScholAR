using System;
using System.Threading;
using System.Threading.Tasks;
using NeuroScholAR.Core.Eeg;
using NUnit.Framework;

namespace NeuroScholAR.Core.Tests
{
    public class ConnectionTests
    {
        private static readonly Func<TimeSpan, CancellationToken, Task> NoWait = (_, __) => Task.CompletedTask;

        private static SimulatedEegSource Sim(EegErrorKind? fail = null) =>
            new SimulatedEegSource(new SimulatedEegOptions { RealTime = false, FailConnectWith = fail });

        private static void WaitFor(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!condition() && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(5);
            }

            Assert.That(condition(), Is.True, "condition not reached in time");
        }

        [TestCase(EegErrorKind.BluetoothUnavailable)]
        [TestCase(EegErrorKind.PermissionDenied)]
        public async Task ConnectFailure_IsReported_AndLightIsRed(EegErrorKind kind)
        {
            using var manager = new EegConnectionManager(Sim(kind), NoWait);
            Assert.That(await manager.ConnectAsync(default), Is.False);
            Assert.That(manager.LastError.Kind, Is.EqualTo(kind));
            Assert.That(EegConnectionManager.Light(manager.State, null), Is.EqualTo(ConnectionLight.Red));
            Assert.That(manager.TakeConnectionLostAlert(), Is.False, "a failed connect is not a lost connection");
            Assert.That(manager.State, Is.EqualTo(EegConnectionState.Disconnected), "no background reconnect");
        }

        [Test]
        public async Task LinkLoss_AlertsOnce_ThenReconnectsAfterFailures()
        {
            var sim = Sim();
            using var manager = new EegConnectionManager(sim, NoWait);
            Assert.That(await manager.ConnectAsync(default), Is.True);

            sim.SimulateLinkLoss(failedReconnects: 2);
            Assert.That(manager.TakeConnectionLostAlert(), Is.True);
            Assert.That(manager.TakeConnectionLostAlert(), Is.False);

            WaitFor(() => manager.State == EegConnectionState.Connected);
            Assert.That(manager.ReconnectCount, Is.EqualTo(1));
        }

        [Test]
        public async Task UserDisconnect_DoesNotAlertOrReconnect()
        {
            using var manager = new EegConnectionManager(Sim(), NoWait);
            await manager.ConnectAsync(default);
            manager.Disconnect();
            Assert.That(manager.TakeConnectionLostAlert(), Is.False);
            Assert.That(manager.State, Is.EqualTo(EegConnectionState.Disconnected));
        }

        [Test]
        public void Light_YellowWhenAnElectrodeIsWeak() =>
            Assert.That(EegConnectionManager.Light(EegConnectionState.Connected, new[] { true, false }), Is.EqualTo(ConnectionLight.Yellow));
    }
}
