using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace ProceduralGeneration.SatContext
{
    /// <summary>
    /// Client for the SAT-tile-Segmentation tile API (<c>/api/tile-requests</c>).
    /// Asks the server to segment one Web Mercator tile and write
    /// <see cref="ColorFile"/>, <see cref="HeightFile"/>, <see cref="SegmentationFile"/> and
    /// <see cref="MetadataFile"/> into a local folder.
    /// The server writes the files itself, so it has to run on this machine (python app.py).
    /// Works in edit and play mode, continuations run on the main thread.
    /// </summary>
    public static class SegmentationApiClient
    {
        public const string ColorFile = "color.png";
        public const string HeightFile = "height.npy";
        public const string SegmentationFile = "segmentation_classes.tif";
        public const string MetadataFile = "metadata.json";

        /// <summary>Where app.py is listening.</summary>
        public static string baseUrl = "http://127.0.0.1:5000";

        /// <summary>Timeout of a single HTTP call in seconds, not of the whole tile request.</summary>
        public static int requestTimeout = 30;

        /// <summary>
        /// Submits <paramref name="tile"/> and waits until its files are written to <paramref name="outputDir"/>.
        /// Throws <see cref="SegmentationApiException"/> if the server refuses the request or the job fails.
        /// Cancelling stops waiting, the server still finishes the job.
        /// </summary>
        /// <param name="colorZoom">Zoom of color.png and segmentation_classes.tif, e.g. 17.</param>
        /// <param name="heightZoom">Zoom of height.npy, e.g. 15. Kept at its own resolution.</param>
        /// <param name="outputDir">Folder for the files, relative paths are resolved against the project folder.</param>
        /// <param name="progress">Receives every polled status.</param>
        public static async Task<TileRequestStatus> RequestTileAsync(
            TileBounds tile,
            int colorZoom,
            int heightZoom,
            string outputDir,
            IProgress<TileRequestStatus> progress = null,
            float pollInterval = 1f,
            CancellationToken cancellationToken = default)
        {
            TileRequestStatus status = await SubmitAsync(tile, colorZoom, heightZoom, outputDir, cancellationToken);
            progress?.Report(status);
            while (!status.isFinished)
            {
                await Task.Delay(TimeSpan.FromSeconds(pollInterval), cancellationToken);
                status = await GetStatusAsync(status.id, cancellationToken);
                progress?.Report(status);
            }

            if (status.isFailed)
                throw new SegmentationApiException($"Tile {Describe(tile)} failed: {status.error}", 0);
            return status;
        }

        /// <summary>Queues <paramref name="tile"/> and returns right away, poll it with <see cref="GetStatusAsync"/>.</summary>
        public static async Task<TileRequestStatus> SubmitAsync(
            TileBounds tile,
            int colorZoom,
            int heightZoom,
            string outputDir,
            CancellationToken cancellationToken = default)
        {
            var body = new TileRequestBody
            {
                tile = new TileId { z = tile.zoom, x = tile.x, y = tile.y },
                colorZoom = colorZoom,
                heightZoom = heightZoom,
                outputDir = Path.GetFullPath(outputDir),
            };
            var request = new UnityWebRequest($"{baseUrl}/api/tile-requests", UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonUtility.ToJson(body))),
                downloadHandler = new DownloadHandlerBuffer(),
            };
            request.SetRequestHeader("Content-Type", "application/json");
            string json = await SendAsync(request, cancellationToken);
            return JsonUtility.FromJson<TileRequestStatus>(json);
        }

        public static async Task<TileRequestStatus> GetStatusAsync(string requestId, CancellationToken cancellationToken = default)
        {
            string json = await SendAsync(
                UnityWebRequest.Get($"{baseUrl}/api/tile-requests/{UnityWebRequest.EscapeURL(requestId)}"),
                cancellationToken);
            return JsonUtility.FromJson<TileRequestStatus>(json);
        }

        /// <summary>True if the server is reachable and has a segmentation model loaded.</summary>
        public static async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                string json = await SendAsync(UnityWebRequest.Get($"{baseUrl}/healthz"), cancellationToken);
                return JsonUtility.FromJson<HealthBody>(json).segmentation;
            }
            catch (SegmentationApiException)
            {
                return false;
            }
        }

        private static async Task<string> SendAsync(UnityWebRequest request, CancellationToken cancellationToken)
        {
            using (request)
            {
                cancellationToken.ThrowIfCancellationRequested();
                request.timeout = requestTimeout;
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken)))
                {
                    request.SendWebRequest().completed += _ => completion.TrySetResult(true);
                    try
                    {
                        await completion.Task;
                    }
                    catch (OperationCanceledException)
                    {
                        request.Abort();
                        throw;
                    }
                }

                if (request.result == UnityWebRequest.Result.Success)
                    return request.downloadHandler.text;

                if (request.result == UnityWebRequest.Result.ProtocolError)
                {
                    // The server explains every refusal as {"error": "..."}.
                    string reason = request.error;
                    try
                    {
                        var error = JsonUtility.FromJson<ErrorBody>(request.downloadHandler.text);
                        if (!string.IsNullOrEmpty(error?.error))
                            reason = error.error;
                    }
                    catch (ArgumentException)
                    {
                        // Not JSON, keep the HTTP status text.
                    }
                    throw new SegmentationApiException(reason, request.responseCode);
                }

                throw new SegmentationApiException(
                    $"Could not reach the segmentation server at {baseUrl} ({request.error}). Is app.py running?",
                    0);
            }
        }

        private static string Describe(TileBounds tile) => $"z{tile.zoom}/{tile.x}/{tile.y}";

        [Serializable]
        public class TileId
        {
            public int z;
            public int x;
            public int y;
        }

        /// <summary>Absolute paths of the written files.</summary>
        [Serializable]
        public class TileRequestFiles
        {
            public string color;
            public string height;
            public string segmentation;
            public string metadata;
        }

        /// <summary>
        /// Mirrors the server's status JSON. <see cref="files"/>, the sizes and <see cref="warnings"/>
        /// are only filled in once <see cref="status"/> is "done".
        /// </summary>
        [Serializable]
        public class TileRequestStatus
        {
            public string id;
            /// <summary>queued | running | done | error</summary>
            public string status;
            /// <summary>0 to 1.</summary>
            public float progress;
            public string message;
            public string error;
            public TileId tile;
            public int colorZoom;
            public int heightZoom;
            public string outputDir;
            public float elapsedSeconds;
            public TileRequestFiles files;
            /// <summary>[width, height] of color.png and segmentation_classes.tif.</summary>
            public int[] colorSizePx;
            /// <summary>[width, height] of height.npy.</summary>
            public int[] heightSizePx;
            public string[] warnings;

            public bool isDone => status == "done";
            public bool isFailed => status == "error";
            public bool isFinished => isDone || isFailed;
        }

        [Serializable]
        private class TileRequestBody
        {
            public TileId tile;
            public int colorZoom;
            public int heightZoom;
            public string outputDir;
        }

        // Only ever filled in by JsonUtility.
#pragma warning disable CS0649
        [Serializable]
        private class ErrorBody
        {
            public string error;
        }

        [Serializable]
        private class HealthBody
        {
            public bool segmentation;
        }
#pragma warning restore CS0649
    }

    public class SegmentationApiException : Exception
    {
        /// <summary>HTTP status code, 0 if the server was unreachable or the job itself failed.</summary>
        public readonly long statusCode;

        public SegmentationApiException(string message, long statusCode) : base(message)
        {
            this.statusCode = statusCode;
        }
    }
}
