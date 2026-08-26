using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using MainCore.Data;
using MainCore.Utilities;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

namespace MainCore
{
    public class VideoManager : MonoBehaviour
    {
        [SerializeField] private GameObject videoRoot;
        private RawImage rawImage;
        private RenderTexture renderTexture;
        private VideoPlayer videoPlayer;
        private bool isInited;
        private Video[] videos;
        private Color[] targetColors;
        private double[] durations;
        private int videoIndex;
        private bool inVideo = false;
        private bool isPrepared;

        public void Init()
        {
            Debug.Log("[VideoManager] Init 被调用, Videos=" + (GlobalSetting.CurrentBeatmapInfo.ExtraEvents?.Videos?.Count) + ", Effects=" + (GlobalSetting.CurrentBeatmapInfo.ExtraEvents?.Effects?.Count));
            if (isInited) return;
            isInited = true;

            if (videoRoot == null)
            {
                Debug.LogWarning("[VideoManager] videoRoot 未赋值，禁用视频");
                return;
            }

            // CameraFarPlane 模式直接渲染到相机，不需要 Canvas/RawImage
            rawImage = null;
            List<Video> list = GlobalSetting.CurrentBeatmapInfo.ExtraEvents.Videos;
            if (list.Count < 1)
            {
                Destroy(videoRoot);
                return;
            }

            list.Sort((v1, v2) => v1.time.Frac() < v2.time.Frac() ? -1 : 1);
            videos = list.ToArray();
            Debug.Log($"[VideoManager] 视频数量={videos.Length}, ChartPath={GlobalSetting.CurrentBeatmapInfo.ChartPath}, BasePath={GlobalSetting.CurrentBeatmapInfo.BasePath}");
            for (int vi = 0; vi < videos.Length; vi++)
            {
                Debug.Log($"[VideoManager] 视频[{vi}] path={videos[vi].path} 本地文件={File.Exists(ResolveVideoPath(videos[vi]))} Resources={ResolveVideoClipPath(videos[vi])}");
            }

            targetColors = new Color[videos.Length];
            for (int i = 0; i < targetColors.Length; i++)
            {
                float grayscale = Mathf.Clamp01(1 - videos[i].dim);
                targetColors[i] = new Color(grayscale, grayscale, grayscale, videos[i].alpha);
            }

            durations = new double[videos.Length];

            // 创建 VideoPlayer（挂在 videoRoot 上）
            videoPlayer = videoRoot.GetComponent<VideoPlayer>();
            if (videoPlayer == null)
            {
                videoPlayer = videoRoot.AddComponent<VideoPlayer>();
            }
            videoPlayer.playOnAwake = false;
            videoPlayer.skipOnDrop = true;
            videoPlayer.audioOutputMode = VideoAudioOutputMode.None;
            videoPlayer.renderMode = VideoRenderMode.CameraFarPlane;
            videoPlayer.targetCamera = Camera.main;
            // 保持视频纵横比并铺满整个屏幕（超出部分裁剪），任意分辨率都满屏
            videoPlayer.aspectRatio = VideoAspectRatio.FitOutside;
            videoPlayer.prepareCompleted += OnPrepareCompleted;

            WaitForPlay();
        }

        private string ResolveVideoPath(Video video)
        {
            string path = Path.Combine(GlobalSetting.CurrentBeatmapInfo.BasePath, video.path);
            if (File.Exists(path)) return path;

            // 内部谱面（Resources/Charts）：编辑器下用真实文件路径
            string chartPath = GlobalSetting.CurrentBeatmapInfo.ChartPath;
            if (!string.IsNullOrEmpty(chartPath))
            {
                string folder = Path.GetDirectoryName(chartPath)?.Replace('\\', '/') ?? "";
                string editorPath = Path.Combine(Application.dataPath, "Resources", folder.Replace('/', Path.DirectorySeparatorChar), video.path);
                if (File.Exists(editorPath)) return editorPath;
            }

            return path;
        }

        private string ResolveVideoClipPath(Video video)
        {
            // 内部谱面：Resources 路径（无扩展名）
            string chartPath = GlobalSetting.CurrentBeatmapInfo.ChartPath;
            if (!string.IsNullOrEmpty(chartPath))
            {
                string folder = Path.GetDirectoryName(chartPath)?.Replace('\\', '/') ?? "";
                return $"{folder}/{Path.GetFileNameWithoutExtension(video.path)}";
            }
            return null;
        }

        private VideoClip LoadVideoClip(Video video)
        {
            string clipPath = ResolveVideoClipPath(video);
            if (!string.IsNullOrEmpty(clipPath))
            {
                return Resources.Load<VideoClip>(clipPath);
            }
            return null;
        }

        private void OnPrepareCompleted(VideoPlayer vp)
        {
            isPrepared = true;
            Debug.Log($"[VideoManager] 视频准备完成: 宽高={vp.width}x{vp.height}, 时长={vp.length}");
        }

        public void Pause()
        {
            if (inVideo && videoPlayer != null && videoPlayer.isPlaying)
            {
                try { videoPlayer.Pause(); } catch { }
            }
        }

        private async void WaitForPlay()
        {
            Debug.Log($"[VideoManager] WaitForPlay 开始, videos.Length={videos?.Length}, rawImage={rawImage != null}");
            for (videoIndex = 0; videoIndex < videos.Length; videoIndex++)
            {
                if (videoIndex > 0)
                    await UniTask.WaitWhile(() =>
                        videos[videoIndex - 1].realTime + durations[videoIndex - 1] >
                        Main.Instance.progressManager.NowTime);
                inVideo = false;
                if (rawImage != null) rawImage.color = Color.clear;
                await UniTask.WaitWhile(() =>
                    videos[videoIndex].realTime < Main.Instance.progressManager.NowTime || GlobalSetting.Paused);

                inVideo = true;
                if (rawImage != null) rawImage.color = targetColors[videoIndex];

                var video = videos[videoIndex];

                // 选择视频源：优先本地文件（file://），否则 Resources VideoClip
                string localPath = ResolveVideoPath(video);
                VideoClip clip = null;
                if (!File.Exists(localPath))
                {
                    clip = LoadVideoClip(video);
                }
                if (clip != null)
                {
                    videoPlayer.clip = clip;
                    videoPlayer.url = null;
                }
                else
                {
                    videoPlayer.clip = null;
                    videoPlayer.url = "file:///" + localPath.Replace('\\', '/');
                }

                // 准备视频并播放
                isPrepared = false;
                videoPlayer.Prepare();
                float waitTime = 0;
                while (!isPrepared && !videoPlayer.isPrepared)
                {
                    await UniTask.Delay(100);
                    waitTime += 0.1f;
                    if (waitTime > 15) break;
                }
                if (isPrepared || videoPlayer.isPrepared)
                {
                    durations[videoIndex] = videoPlayer.length;
                    videoPlayer.Play();
                    Debug.Log($"[VideoManager] 播放中: isPlaying={videoPlayer.isPlaying}, renderMode={videoPlayer.renderMode}, frame={videoPlayer.frame}, time={videoPlayer.time}");
                }
                else
                {
                    Debug.LogError($"[VideoManager] 视频准备超时: {video.path}");
                }
            }

            rawImage.color = Color.clear;
        }

        public void Resume()
        {
            if (inVideo && videoPlayer != null && videoPlayer.isPaused)
            {
                try { videoPlayer.Play(); } catch { }
            }
        }

        public void OnDestroy()
        {
            if (renderTexture != null) renderTexture.Release();
        }
    }
}
