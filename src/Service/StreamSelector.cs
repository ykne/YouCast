using System;
using System.Linq;
using MoreLinq;
using YoutubeExplode.Videos.Streams;

namespace Service
{
    public static class StreamSelector
    {
        /// <summary>Height requested by an encoding such as "MP4_720p"; defaults to 360.</summary>
        public static int ParseHeight(string encoding)
        {
            try
            {
                return int.Parse(encoding.Remove(encoding.Length - 1).Substring(startIndex: 4));
            }
            catch
            {
                return 360;
            }
        }

        /// <summary>
        /// Picks the audio stream: MP4/AAC first (iOS podcast apps only play it), the original-language track
        /// (YouTube also offers auto-dubbed ones), then the highest bitrate.
        /// </summary>
        public static AudioOnlyStreamInfo SelectAudio(StreamManifest manifest, string videoId)
        {
            var audios = manifest.GetAudioOnlyStreams().ToList();

            var mp4Audios = audios.Where(audio => audio.Container == Container.Mp4).ToList();
            if (mp4Audios.Count == 0 && audios.Count > 0)
            {
                ExceptionHandler.Handle(new InvalidOperationException($"No MP4 audio stream for video '{videoId}'; falling back to another container."));
            }

            var candidates = mp4Audios.Count > 0 ? mp4Audios : audios;

            var originals = candidates.Where(audio => audio.IsAudioLanguageDefault == true).ToList();
            if (originals.Count > 0)
            {
                candidates = originals;
            }
            else if (candidates.Any(audio => audio.AudioLanguage != null))
            {
                ExceptionHandler.Handle(new InvalidOperationException($"Video '{videoId}' has several audio languages but none is marked as the original."));
            }

            return candidates.Maxima(audio => audio.Bitrate).FirstOrDefault();
        }

        /// <summary>
        /// Picks an H.264 (avc1) MP4 video-only stream: the highest resolution not above <paramref name="height"/>,
        /// otherwise the lowest one above it. Returns null when there is no H.264 MP4 stream.
        /// </summary>
        public static VideoOnlyStreamInfo SelectVideo(StreamManifest manifest, int height)
        {
            var videos = manifest.GetVideoOnlyStreams().
                Where(video =>
                    video.Container == Container.Mp4 &&
                    video.VideoCodec.StartsWith("avc1", StringComparison.OrdinalIgnoreCase)).
                ToList();

            var below = videos.Where(video => video.VideoResolution.Height <= height).ToList();
            var candidates = below.Count > 0
                ? below.Maxima(video => video.VideoResolution.Height).ToList()
                : videos.Minima(video => video.VideoResolution.Height).ToList();

            return candidates.Maxima(video => video.Bitrate).FirstOrDefault();
        }
    }
}
