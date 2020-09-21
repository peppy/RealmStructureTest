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
        [Test]
        public void TestBasicWrite()
        {
            var realm = Game.GetRealmInstance();
            performBasicWrite(realm);
            performBasicRead(realm);
        }

        /// <summary>
        /// This tests an asynchronous realm query using IQueryable.
        /// The callback will be run in the game thread context (on next realm.Refresh call).
        /// </summary>
        [Test]
        public void TestAsyncViaSubscription()
        {
            var game = new Game();

            game.ScheduleRealm(realm =>
            {
                realm.All<BeatmapInfo>().Where(o => true /* expensive lookup */).SubscribeForNotifications((sender, changes, error) =>
                {
                    // scheduled to GameBase automatically.
                    game.Exit();
                });
            });

            game.WaitForExit();
        }

        [Test]
        public void TestFreezeFromOtherThread()
        {
            var realm = Game.GetRealmInstance();
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
            var realm = Game.GetRealmInstance();
            Assert.Greater(realm.All<BeatmapInfo>().Count(), 0);

            // performing an await here will start a state machine
            // we can't be guaranteed to return to the original thread the realm is bound to.
            await Task.Run(() =>
            {
                /* this doesn't need to be realm work for a failure */
            });

            Exception thrown = null;
            
            try
            {
                // as a result, this will throw.
                // this can be avoided by creating a custom SynchronizationContext.
                // we should investigate this further if it's decided that we want to use async in more places.
                performBasicRead(realm);
            }
            catch (Exception e)
            {
                thrown = e;
            }

            Assert.NotNull(thrown);
        }

        private void performBasicWrite(Realm realm)
        {
            var transaction = realm.BeginWrite();
            realm.Add(new BeatmapInfo("test1"));
            transaction.Commit();
            realm.Refresh();
        }


        private void performBasicRead(Realm realm)
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
        private Realm realm;
        private ManualResetEventSlim exitRequested = new ManualResetEventSlim();

        public static Realm GetRealmInstance() => Realm.GetInstance("test.realm");

        public Game()
        {
            var mainThread = new Thread(Update)
            {
                IsBackground = true
            };

            mainThread.Start();
        }

        public void Exit()
        {
            exitRequested.Set();

        }

        private void Update()
        {
            // must be initialised on the update thread
            realm ??= GetRealmInstance();

            while (scheduledActions.TryPop(out var action))
                action();

            realm.Refresh();
        }

        public void WaitForExit() => exitRequested.Wait();
        
        public void Schedule(Action action) => scheduledActions.Push(action);

        public void ScheduleRealm(Action<Realm> action) => scheduledActions.Push(() => action(realm));
    }

    public class TestSynchronizationContext : SynchronizationContext
    {
        // TODO: reimplement
    }
}