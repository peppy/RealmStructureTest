using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Realms;

namespace RealmStructureTest
{
    public class Tests
    {
        private Realm realm;

        [SetUp]
        public void Setup()
        {
            realm = Realm.GetInstance("test.realm");

            performBasicWrite();

            realm.Refresh();
        }

        [Test]
        public void TestBasicWrite()
        {
            performBasicRead();
        }

        [Test]
        public void TestAsyncLookup()
        {
            var reset = new ManualResetEventSlim();

            realm.All<BeatmapInfo>().Where(o => true /* expensive lookup */).SubscribeForNotifications((sender, changes, error) =>
            {
                // scheduled to GameBase automatically.
                reset.Set();
            });

            while (true)
            {
                realm.Refresh();

                if (reset.IsSet)
                    break;
            }
        }

        [Test]
        public void TestFreezeFromOtherThread()
        {
            var reset = new ManualResetEventSlim();

            Task.Run(() =>
            {
                var frozen = realm.Freeze();
                var retrieved = frozen.All<BeatmapInfo>();

                var items = retrieved.ToList();
                Assert.Greater(items.Count, 0);
            }).ContinueWith(task =>
            {
                // this isn't a valid operation.
                // the freeze must be performed on the thread which the realm is bound to.
                Assert.NotNull(task.Exception);
                reset.Set();
            });

            while (true)
            {
                realm.Refresh();

                if (reset.IsSet)
                    break;
            }
        }

        [Test]
        public async Task TestWriteWithAsyncOperationInBetween()
        {
            Assert.Greater(realm.All<BeatmapInfo>().Count(), 0);
            var reset = new ManualResetEventSlim();

            // performing an await here will start a state machine
            // we can't be guaranteed to return to the original thread the realm is bound to.
            await Task.Run(performBasicRead);

            // as a result, this will throw.
            // this can be avoided by creating a custom SynchronizationContext.
            // we should investigate this further if it's decided that we want to use async in more places.
            performBasicRead();

            while (true)
            {
                realm.Refresh();
                if (reset.IsSet)
                    break;
            }
        }

        private void performBasicWrite()
        {
            var transaction = realm.BeginWrite();
            realm.Add(new BeatmapInfo("test1"));
            transaction.Commit();
        }


        private void performBasicRead()
        {
            Assert.Greater(realm.All<BeatmapInfo>().Count(), 0);

            var items = realm.All<BeatmapInfo>();
            Assert.AreEqual(items.First().Title, "test1");
        }
    }

    /// <summary>
    /// Represents a cut-down version of BeatmapInfo for simplicity.
    /// </summary>
    public class BeatmapInfo : RealmObject
    {
        public string Title { get; set; }

        public BeatmapInfo(string title)
        {
            Title = title;
        }

        public BeatmapInfo()
        {
        }
    }

    /// <summary>
    /// Represents OsuGame.
    /// </summary>
    public class Game
    {
        private readonly ConcurrentStack<Action> scheduledActions = new ConcurrentStack<Action>();
        private readonly Realm realm;

        public Game()
        {
            realm = Realm.GetInstance("test.realm");

            var mainThread = new Thread(Update)
            {
                IsBackground = true
            };

            mainThread.Start();
        }

        private void Update()
        {
            while (scheduledActions.TryPop(out var action))
                action();

            realm.Refresh();
        }
    }

    public class TestSynchronizationContext : SynchronizationContext
    {
        // TODO: reimplement
    }
}