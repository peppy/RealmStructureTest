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
            var realm = Game.GetFreshRealmInstance();
            performBasicWrite(realm);
            performBasicRead(realm);
        }

        [Test]
        public void TestFreezeFromOtherThreadFails()
        {
            var realm = Game.GetFreshRealmInstance();
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
        public async Task TestWriteWithAsyncOperationInBetweenFails()
        {
            var realm = Game.GetFreshRealmInstance();

            performBasicWrite(realm);

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

        /// <summary>
        /// This tests an asynchronous realm query using IQueryable.
        /// The callback will be run in the game thread context (on next realm.Refresh call).
        /// </summary>
        [Test]
        public void TestAsyncViaSubscription()
        {
            var game = new Game();
            int retrievedCount = 0;

            game.ScheduleRealm(realm =>
            {
                performBulkWrite(realm, 1000);

                var query = realm.All<BeatmapInfo>().Where(o => true /* expensive lookup here */);

                query.SubscribeForNotifications((sender, changes, error) =>
                {
                    retrievedCount = query.Count();

                    // scheduled to GameBase automatically.
                    game.Exit();
                });
            });

            game.WaitForExit();

            Assert.AreEqual(1000, retrievedCount);
        }

        /// <summary>
        /// Aims to test how lazer loads and filters beatmaps for song select in its *current state*.
        /// This may not be optimal for realm, but can be used as a baseline and benchmark.
        /// </summary>
        /// <remarks>
        /// - SongSelect loads BeatmapCarousel asynchronously.
        /// - BeatmapCarousel loads all beatmaps into a local list in its BDL.
        /// - FilterControl applies filter conditions (recursively down the CarouselItem tree) synchronously on any change.
        /// </remarks>
        [Test]
        public void TestSongSelectLoadFilterFlow()
        {
            var game = new Game();

            const int beatmap_count = 1_000_000;

            game.ScheduleRealmWrite(r => performBulkWrite(r, beatmap_count));

            int foundCount = game.ScheduleRealm(r => r.All<BeatmapInfo>().Count());
            Assert.AreEqual(beatmap_count, foundCount);

            game.ExitAndWait();
        }

        private void performBulkWrite(Realm realm, int count = 100000)
        {
            var transaction = realm.BeginWrite();

            for (int i = 0; i < count; i++)
                realm.Add(new BeatmapInfo($"test-{count}"));

            transaction.Commit();
        }

        private void performBasicWrite(Realm realm)
        {
            var transaction = realm.BeginWrite();
            realm.Add(new BeatmapInfo("test1"));
            transaction.Commit();
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
    public class Game : UpdateableComponent
    {
        private Realm realm;

        private readonly ConcurrentBag<UpdateableComponent> components = new ConcurrentBag<UpdateableComponent>();

        private readonly ManualResetEventSlim exitRequested = new ManualResetEventSlim();

        public Realm GetGameRealm() => Realm.GetInstance(realm.Config);

        public static Realm GetFreshRealmInstance() => Realm.GetInstance($"test_{Guid.NewGuid()}");

        public Game()
        {
            var mainThread = new Thread(Update)
            {
                IsBackground = true
            };

            mainThread.Start();
        }

        public void ExitAndWait()
        {
            Exit();
            WaitForExit();
        }

        public void Exit()
        {
            Schedule(() => exitRequested.Set());
        }

        public void Add(UpdateableComponent component) => components.Add(component);

        public override void Update()
        {
            while (true)
            {
                // must be initialised on the update thread
                realm ??= GetFreshRealmInstance();

                base.Update();

                foreach (var c in components)
                    c.Update();

                realm.Refresh();
            }

            // ReSharper disable once FunctionNeverReturns
        }

        public void ScheduleRealm(Action<Realm> action) => Schedule(() => action(realm));

        public void ScheduleRealmWrite(Action<Realm> action)
        {
            Schedule(() =>
            {
                action(realm);

                // would usually happen on the next update frame.
                // this is just to simplify the writing of tests.
                realm.Refresh();
            });
        }

        public T ScheduleRealm<T>(Func<Realm, T> action)
        {
            T val = default;

            var reset = new ManualResetEventSlim();

            Schedule(() =>
            {
                val = action(realm);
                reset.Set();
            });

            reset.Wait();

            return val;
        }

        public void WaitForExit() => exitRequested.Wait();
    }

    public abstract class UpdateableComponent
    {
        private readonly ConcurrentQueue<Action> scheduledActions = new ConcurrentQueue<Action>();

        public void Schedule(Action action) => scheduledActions.Enqueue(action);

        public virtual void Update()
        {
            while (scheduledActions.TryDequeue(out var action))
                action();
        }
    }

    public class TestSynchronizationContext : SynchronizationContext
    {
        // TODO: reimplement
    }
}