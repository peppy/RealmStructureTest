using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Realms;

namespace RealmStructureTest
{
    public class Tests
    {
        private Stopwatch sw;

        [SetUp]
        public void Setup()
        {
            sw = new Stopwatch();
            sw.Start();
        }

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

            logTime("bulk write completed");

            int foundCount = game.ScheduleRealm(r => r.All<BeatmapInfo>().Count());
            Assert.AreEqual(beatmap_count, foundCount);

            logTime("count lookup completed");

            var filtered = game.ScheduleRealm(r =>
            {
                // BeatmapCarousel currently does this on BDL.
                // as long as we are retrieving the IQueryable/IEnumerable this is a free query, so unnecessary to async it.
                var usableBeatmaps = r.All<BeatmapInfo>().Where(b => !b.DeletePending);

                // just here for testing purposes (so we can run queries below outside the game context)
                usableBeatmaps = usableBeatmaps.Freeze();


                // current filter logic is run synchronously
                var filteredBeatmaps = usableBeatmaps.AsEnumerable().Where(b => isInRange(b.Difficulty)).ToList();

                return filteredBeatmaps;
            });

            game.ExitAndWait();

            logTime("filter completed");

            Console.WriteLine($"post filter results: {filtered.Count()}");

            logTime("filter count completed");

            logTime($"sum filtered difficulties (backed): {filtered.Sum(f => f.Difficulty)}");
            logTime($"sum filtered difficulties (unbacked): {filtered.Sum(f => f.DifficultyUnbacked)}");

            filtered = filtered.ToList();
            logTime("convert to list");

            logTime($"sum filtered difficulties (backed): {filtered.Sum(f => f.Difficulty)}");
            logTime($"sum filtered difficulties (unbacked): {filtered.Sum(f => f.DifficultyUnbacked)}");
        }

        [Test]
        public void TestSongSelectLoadFilterFlowReferenceResolve()
        {
            var game = new Game();

            const int beatmap_count = 1_000_000;

            game.ScheduleRealmWrite(r => performBulkWrite(r, beatmap_count));

            logTime("bulk write completed");

            int foundCount = game.ScheduleRealm(r => r.All<BeatmapInfo>().Count());
            Assert.AreEqual(beatmap_count, foundCount);

            logTime("count lookup completed");

            var threadSafeFiltered = game.ScheduleRealm(r =>
            {
                // BeatmapCarousel currently does this on BDL.
                // as long as we are retrieving the IQueryable/IEnumerable this is a free query, so unnecessary to async it.
                var usableBeatmaps = r.All<BeatmapInfo>().Where(b => !b.DeletePending);

                // current filter logic is run synchronously
                var filteredBeatmaps = usableBeatmaps;

                return ThreadSafeReference.Create(filteredBeatmaps);
            });

            game.ExitAndWait();

            logTime("filter completed");
            
            var localThreadRealm = game.GetGameRealm();
            var filtered = localThreadRealm.ResolveReference(threadSafeFiltered).AsEnumerable().Where(b => isInRange(b.Difficulty)).ToList();

            logTime("filter resolved to list");

            Console.WriteLine($"post filter results: {filtered.Count()}");

            logTime("filter count completed");

            logTime($"sum filtered difficulties (backed): {filtered.Sum(f => f.Difficulty)}");
            logTime($"sum filtered difficulties (unbacked): {filtered.Sum(f => f.DifficultyUnbacked)}");

            filtered = filtered.ToList();
            logTime("convert to list");

            logTime($"sum filtered difficulties (backed): {filtered.Sum(f => f.Difficulty)}");
            logTime($"sum filtered difficulties (unbacked): {filtered.Sum(f => f.DifficultyUnbacked)}");
        }

        private long lastLogTime;

        private void logTime(string text)
        {
            var newLogTime = sw.ElapsedMilliseconds;

            Console.WriteLine($"{newLogTime.ToString().PadRight(10)} ({(newLogTime - lastLogTime).ToString().PadRight(10)}): {text}");

            lastLogTime = newLogTime;
        }

        /// <summary>
        /// Sample filter method which likely can't be performed via IQueryable
        /// </summary>
        private bool isInRange(double value) => value > 5 && value < 8;

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
        private static int deleted;

        public string Title { get; set; }

        public bool DeletePending { get; set; }

        public double DifficultyUnbacked = rng.NextDouble() * 10;

        public double Difficulty { get; set; }

        private static readonly Random rng = new Random();

        public BeatmapInfo(string title)
        {
            Title = title;

            // for testing, one in ten beatmaps will be pending deletion.
            DeletePending = deleted++ % 10 == 0;

            Difficulty = rng.NextDouble() * 10;
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
            var reset = new ManualResetEventSlim();

            Schedule(() =>
            {
                action(realm);

                // would usually happen on the next update frame.
                // this is just to simplify the writing of tests.
                realm.Refresh();

                reset.Set();
            });

            reset.Wait();
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