using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ProceduralGeneration.SatContext;
using UnityEditor;
using UnityEngine;

namespace ProceduralGeneration.Editor
{
    public enum TileDownloadState
    {
        Queued,
        Running,
        Done,
        /// <summary>The files were already in the folder.</summary>
        Skipped,
        Failed,
        Cancelled,
    }

    /// <summary>One tile of a <see cref="TileDownloadBatch"/>.</summary>
    public class TileDownloadJob
    {
        public TileBounds tile;
        public int colorZoom;
        public int heightZoom;
        /// <summary>Absolute folder the server writes the files into.</summary>
        public string outputDir;

        public TileDownloadState state;
        /// <summary>0 to 1.</summary>
        public float progress;
        public string message = "queued";
        public SatContext.SatContext satContext;

        public bool isFinished => state >= TileDownloadState.Done;
        public string name => TileDownloadBatch.FolderName(tile);
    }

    /// <summary>
    /// Downloads tiles with <see cref="SegmentationApiClient"/>, a few at a time, and creates a
    /// <see cref="SatContext.SatContext"/> next to the files of every downloaded tile inside Assets.
    /// A failed tile is marked and never stops the others. Runs on the main thread.
    /// </summary>
    public class TileDownloadBatch
    {
        public const string SatContextFile = "satContext.asset";
        /// <summary>Pixel size of a source tile, one zoom level up doubles the resolution of a map.</summary>
        public const int SourceTileSize = 256;
        // The server runs one job at a time, the second request queues behind it so the server never idles.
        private const int MaxInFlight = 2;

        public readonly string label;
        public readonly List<TileDownloadJob> jobs;
        public bool showFailed;

        /// <summary>Raised on the main thread whenever a job's progress or state changes.</summary>
        public event Action changed;

        private readonly bool _loadData;
        private readonly bool _skipExisting;
        private readonly bool _clampZooms;
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private int _next;
        private bool _running;

        /// <param name="loadData">Load the images into the SatContext too, not only the metadata.</param>
        /// <param name="skipExisting">Tiles whose files are already in their folder are not requested.</param>
        /// <param name="clampZooms">Lower zooms the server's sources do not have instead of failing the tile.</param>
        public TileDownloadBatch(string label, List<TileDownloadJob> jobs, bool loadData, bool skipExisting, bool clampZooms)
        {
            this.label = label;
            this.jobs = jobs;
            _loadData = loadData;
            _skipExisting = skipExisting;
            _clampZooms = clampZooms;
        }

        public bool isFinished => !_running && CountFinished() == jobs.Count;
        public bool isCancelled => _cancellation.IsCancellationRequested;

        /// <summary>0 to 1 over all jobs.</summary>
        public float progress
        {
            get
            {
                if (jobs.Count == 0)
                    return 1;
                float sum = 0;
                foreach (var job in jobs)
                    sum += job.isFinished ? 1 : job.progress;
                return sum / jobs.Count;
            }
        }

        public int CountFinished() => jobs.FindAll(j => j.isFinished).Count;
        public int Count(TileDownloadState state) => jobs.FindAll(j => j.state == state).Count;

        public string statusText
        {
            get
            {
                if (jobs.Count == 1)
                {
                    var job = jobs[0];
                    return job.isFinished ? $"{job.state}: {job.message}" : $"{job.message} ({job.progress:P0})";
                }

                string text = $"{CountFinished()}/{jobs.Count} tiles";
                int failed = Count(TileDownloadState.Failed);
                if (failed > 0)
                    text += $", {failed} failed";
                int cancelled = Count(TileDownloadState.Cancelled);
                if (cancelled > 0)
                    text += $", {cancelled} cancelled";
                var running = jobs.Find(j => j.state == TileDownloadState.Running);
                if (running != null)
                    text += $"  |  {running.name}: {running.message}";
                return text;
            }
        }

        public void Cancel() => _cancellation.Cancel();

        public async void Start()
        {
            _running = true;
            try
            {
                if (_clampZooms)
                    await ClampZoomsAsync();

                var workers = new List<Task>();
                for (int i = 0; i < Math.Min(MaxInFlight, jobs.Count); i++)
                    workers.Add(RunWorkerAsync());
                await Task.WhenAll(workers);
            }
            catch (OperationCanceledException)
            {
                // Cancelled before the first tile, the jobs are marked below.
            }
            catch (Exception e)
            {
                Logger.Logger.LogError($"Download '{label}' stopped: {e.Message}", "ProcGen");
            }
            finally
            {
                // Jobs that never started, so the batch still finishes and can be cleared.
                foreach (var job in jobs)
                {
                    if (job.isFinished)
                        continue;
                    job.state = isCancelled ? TileDownloadState.Cancelled : TileDownloadState.Failed;
                    job.message = isCancelled ? "cancelled" : "not started";
                }
                _running = false;
                // Imports the downloaded files.
                AssetDatabase.Refresh();
                changed?.Invoke();
            }
        }

        /// <summary>Lowers zooms the default sources do not have, a server that cannot be asked refuses them per tile instead.</summary>
        private async Task ClampZoomsAsync()
        {
            SegmentationApiClient.ServerConfig config;
            try
            {
                config = await SegmentationApiClient.GetConfigAsync(_cancellation.Token);
            }
            catch (Exception e) when (e is SegmentationApiException || e is ArgumentException)
            {
                return;
            }

            var imagery = config.defaultImagery;
            var terrain = config.defaultTerrain;
            int clampedColor = 0, clampedHeight = 0;
            foreach (var job in jobs)
            {
                if (imagery != null && job.colorZoom > imagery.maxZoom)
                {
                    job.colorZoom = imagery.maxZoom;
                    clampedColor++;
                }
                if (terrain != null && job.heightZoom > terrain.maxZoom)
                {
                    job.heightZoom = terrain.maxZoom;
                    clampedHeight++;
                }
            }
            if (clampedColor > 0)
                Logger.Logger.LogWarning($"{clampedColor} tiles use a lower color resolution, {imagery.name} stops at zoom {imagery.maxZoom}.", "ProcGen");
            if (clampedHeight > 0)
                Logger.Logger.LogWarning($"{clampedHeight} tiles use a lower height resolution, {terrain.name} stops at zoom {terrain.maxZoom}.", "ProcGen");
        }

        private async Task RunWorkerAsync()
        {
            // Single threaded, every continuation is back on the main thread.
            while (_next < jobs.Count)
            {
                var job = jobs[_next++];
                await RunJobAsync(job);
                changed?.Invoke();
            }
        }

        private async Task RunJobAsync(TileDownloadJob job)
        {
            if (_cancellation.IsCancellationRequested)
            {
                job.state = TileDownloadState.Cancelled;
                job.message = "cancelled";
                return;
            }

            try
            {
                // SatContext assets can only be created inside Assets.
                string assetFolder = ToAssetPath(job.outputDir);
                if (assetFolder != null)
                    EnsureAssetFolder(assetFolder);
                else
                    Directory.CreateDirectory(job.outputDir);

                if (_skipExisting && HasFiles(job.outputDir))
                {
                    job.state = TileDownloadState.Skipped;
                    job.message = "already downloaded";
                }
                else
                {
                    job.state = TileDownloadState.Running;
                    var status = await SegmentationApiClient.RequestTileAsync(
                        job.tile, job.colorZoom, job.heightZoom, job.outputDir,
                        new InlineProgress<SegmentationApiClient.TileRequestStatus>(s =>
                        {
                            job.progress = s.progress;
                            job.message = s.message;
                            changed?.Invoke();
                        }),
                        cancellationToken: _cancellation.Token);
                    job.state = TileDownloadState.Done;
                    job.message = status.warnings != null && status.warnings.Length > 0
                        ? string.Join(" ", status.warnings)
                        : $"{status.elapsedSeconds:0.#} s";
                }
                job.progress = 1;

                if (assetFolder != null)
                    job.satContext = CreateSatContext(assetFolder, _loadData);
            }
            catch (OperationCanceledException)
            {
                job.state = TileDownloadState.Cancelled;
                job.message = "cancelled";
            }
            catch (Exception e)
            {
                job.state = TileDownloadState.Failed;
                job.message = e.Message;
                Logger.Logger.LogWarning($"Tile {job.name} failed: {e.Message}", "ProcGen");
            }
        }

        /// <summary>Creates or updates the SatContext of the tile files in <paramref name="assetFolder"/>.</summary>
        public static SatContext.SatContext CreateSatContext(string assetFolder, bool loadData)
        {
            string assetPath = $"{assetFolder}/{SatContextFile}";
            // Update an existing one, references to it stay valid.
            var context = AssetDatabase.LoadAssetAtPath<SatContext.SatContext>(assetPath);
            bool create = context == null;
            if (create)
                context = ScriptableObject.CreateInstance<SatContext.SatContext>();

            // SatContext folders are relative to Assets.
            context.folderPath = assetFolder == "Assets" ? "" : assetFolder.Substring("Assets/".Length);
            if (loadData)
                context.LoadData();
            else
                context.LoadMetadata();

            if (create)
                AssetDatabase.CreateAsset(context, assetPath);
            else
                EditorUtility.SetDirty(context);
            AssetDatabase.SaveAssetIfDirty(context);
            return context;
        }

        /// <summary>z-x-y</summary>
        public static string FolderName(TileBounds tile) => $"{tile.zoom}-{tile.x}-{tile.y}";

        /// <summary>Zoom levels above the tile zoom that give at least <paramref name="resolution"/> pixels per tile.</summary>
        public static int ZoomOffset(int resolution) =>
            Mathf.Max(0, Mathf.CeilToInt(Mathf.Log(resolution / (float)SourceTileSize, 2) - 0.001f));

        private static bool HasFiles(string folder) =>
            File.Exists(Path.Combine(folder, SegmentationApiClient.MetadataFile)) &&
            File.Exists(Path.Combine(folder, SegmentationApiClient.ColorFile)) &&
            File.Exists(Path.Combine(folder, SegmentationApiClient.HeightFile)) &&
            File.Exists(Path.Combine(folder, SegmentationApiClient.SegmentationFile));

        /// <summary>"Assets/..." for a folder inside the project's Assets folder, null otherwise.</summary>
        public static string ToAssetPath(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
                return null;
            // Relative paths are relative to the project folder.
            string full = Path.GetFullPath(folder).Replace('\\', '/').TrimEnd('/');
            string assets = Application.dataPath.Replace('\\', '/');
            if (string.Equals(full, assets, StringComparison.OrdinalIgnoreCase))
                return "Assets";
            if (full.StartsWith(assets + "/", StringComparison.OrdinalIgnoreCase))
                return "Assets" + full.Substring(assets.Length);
            return null;
        }

        /// <summary>Creates the folder through the AssetDatabase, CreateAsset needs a folder it knows about.</summary>
        private static void EnsureAssetFolder(string assetFolder)
        {
            if (AssetDatabase.IsValidFolder(assetFolder))
                return;
            if (Directory.Exists(assetFolder))
            {
                // On disk but not imported yet, CreateFolder would make "name 1".
                AssetDatabase.ImportAsset(assetFolder);
                return;
            }
            string parent = Path.GetDirectoryName(assetFolder).Replace('\\', '/');
            EnsureAssetFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(assetFolder));
        }

        /// <summary>Reports right away, <see cref="Progress{T}"/> would post to the next frame.</summary>
        private sealed class InlineProgress<T> : IProgress<T>
        {
            private readonly Action<T> _report;
            public InlineProgress(Action<T> report) => _report = report;
            public void Report(T value) => _report(value);
        }
    }
}
