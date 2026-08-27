using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using MainCore.Utilities;
using Network.Multiplayer.Managers;
using UnityEngine;
using UnityEngine.Networking;

namespace Network.Chart
{
    public class ChartHandler
    {
        private static Dictionary<string, string> chartMap = new Dictionary<string, string>();
        private static List<string> downloadedCharts = new List<string>();
        private static string UserSpace = "";

        public static string TmpPathRoot => Application.temporaryCachePath;

        private class BypassCertHandler : CertificateHandler
        {
            protected override bool ValidateCertificate(byte[] certificateData)
            {
                return true;
            }
        }

        public static async Task<string> Upload(string folderPath)
        {
            try
            {
                Debug.Log($"[Upload] 开始上传谱面: {folderPath}");
                
                if (string.IsNullOrEmpty(SocketManager.ChartUrlBase))
                {
                    Debug.LogError("[Upload] ChartUrlBase 为空");
                    return "";
                }

                // 准备压缩目录
                string zipDir = Path.Combine(TmpPathRoot, "zip_charts");
                if (!Directory.Exists(zipDir))
                    Directory.CreateDirectory(zipDir);

                string zipPath = Path.Combine(zipDir, Path.GetFileName(folderPath) + ".zip");
                
                // 检查缓存
                if (chartMap.ContainsKey(zipPath))
                {
                    Debug.Log($"[Upload] 使用缓存: {chartMap[zipPath]}");
                    return chartMap[zipPath];
                }

                // 压缩文件（使用 .NET 内置 ZipFile）
                Debug.Log($"[Upload] 压缩文件: {folderPath} -> {zipPath}");
                if (File.Exists(zipPath))
                    File.Delete(zipPath);
                ZipFile.CreateFromDirectory(folderPath, zipPath);

                // 读取压缩文件
                Debug.Log("[Upload] 读取压缩文件");
                byte[] zipData = File.ReadAllBytes(zipPath);
                Debug.Log($"[Upload] 压缩文件大小: {zipData.Length} bytes");

                // 构建上传 URL
                string uploadUrl = SocketManager.ChartUrlBase.UrlCombine("/upload");
                Debug.Log($"[Upload] ChartUrlBase: {SocketManager.ChartUrlBase}");
                Debug.Log($"[Upload] 上传 URL: {uploadUrl}");
                Debug.Log($"[Upload] 文件大小: {zipData.Length} bytes");
                Debug.Log($"[Upload] ServerId: {SocketManager.GetServerId()}");
                Debug.Log($"[Upload] RoomId: {SocketManager.GetRoomId()}");

                // 使用 UnityWebRequest 上传
                Debug.Log("[Upload] 发送请求");
                using (UnityWebRequest request = new UnityWebRequest(uploadUrl, "POST"))
                {
                    // 跳过证书验证（解决打包版 TLS 连接问题）
                    request.certificateHandler = new BypassCertHandler();

                    // 构建 multipart/form-data
                    string boundary = "----UnityBoundary" + DateTime.Now.Ticks.ToString("x");
                    request.SetRequestHeader("Content-Type", "multipart/form-data; boundary=" + boundary);

                    // 构建请求体
                    byte[] body = BuildMultipartFormData(boundary, zipData, Path.GetFileName(zipPath));
                    request.uploadHandler = new UploadHandlerRaw(body);
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.timeout = 60;

                    // 发送请求并等待完成
                    await request.SendWebRequest();

                    Debug.Log($"[Upload] 响应码: {request.responseCode}");
                    Debug.Log($"[Upload] 结果: {request.result}");
                    Debug.Log($"[Upload] 错误: {request.error}");
                    
                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        throw new Exception($"上传失败: {request.error} (HTTP {request.responseCode})");
                    }

                    string responseText = request.downloadHandler.text;
                    Debug.Log($"[Upload] 响应: {responseText}");

                    // 简单解析 JSON（避免使用 Newtonsoft）
                    // 响应格式: {"status":true,"scoreid":"xxx"}
                    if (responseText.Contains("\"status\":true"))
                    {
                        // 提取 scoreid
                        int startIdx = responseText.IndexOf("\"scoreid\":\"") + 11;
                        int endIdx = responseText.IndexOf("\"", startIdx);
                        if (startIdx > 10 && endIdx > startIdx)
                        {
                            string scoreId = responseText.Substring(startIdx, endIdx - startIdx);
                            chartMap[zipPath] = scoreId;
                            Debug.Log($"[Upload] 上传成功: {scoreId}");
                            return scoreId;
                        }
                    }

                    throw new Exception("服务器响应格式错误");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[Upload] 异常: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
                throw;
            }
        }

        private static byte[] BuildMultipartFormData(string boundary, byte[] fileData, string fileName)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                // file 字段
                string fileHeader = $"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{fileName}\"\r\nContent-Type: application/zip\r\n\r\n";
                byte[] fileHeaderBytes = Encoding.UTF8.GetBytes(fileHeader);
                ms.Write(fileHeaderBytes, 0, fileHeaderBytes.Length);
                ms.Write(fileData, 0, fileData.Length);
                ms.Write(Encoding.UTF8.GetBytes("\r\n"), 0, 2);

                // serverid 字段
                string serverIdField = $"--{boundary}\r\nContent-Disposition: form-data; name=\"serverid\"\r\n\r\n{SocketManager.GetServerId()}\r\n";
                byte[] serverIdBytes = Encoding.UTF8.GetBytes(serverIdField);
                ms.Write(serverIdBytes, 0, serverIdBytes.Length);

                // roomid 字段
                string roomIdField = $"--{boundary}\r\nContent-Disposition: form-data; name=\"roomid\"\r\n\r\n{SocketManager.GetRoomId()}\r\n";
                byte[] roomIdBytes = Encoding.UTF8.GetBytes(roomIdField);
                ms.Write(roomIdBytes, 0, roomIdBytes.Length);

                // 结束标记
                string endBoundary = $"--{boundary}--\r\n";
                byte[] endBytes = Encoding.UTF8.GetBytes(endBoundary);
                ms.Write(endBytes, 0, endBytes.Length);

                return ms.ToArray();
            }
        }

        public static async Task<byte[]> Download(string id)
        {
            try
            {
                Debug.Log($"[Download] 开始下载谱面: {id}");

                if (string.IsNullOrEmpty(SocketManager.ChartUrlBase))
                {
                    Debug.LogError("[Download] ChartUrlBase 为空");
                    return null;
                }

                string downloadDir = Path.Combine(TmpPathRoot, "online_charts");
                if (!Directory.Exists(downloadDir))
                    Directory.CreateDirectory(downloadDir);

                string localPath = Path.Combine(downloadDir, id);

                // 检查缓存
                if (downloadedCharts.Contains(id))
                {
                    Debug.Log($"[Download] 使用缓存: {localPath}");
                    return await ReadChartZip(localPath);
                }

                // 构建下载 URL
                string downloadUrl = SocketManager.ChartUrlBase.UrlCombine("/download") + $"?chartid={id}";
                Debug.Log($"[Download] 下载 URL: {downloadUrl}");

                // 使用 UnityWebRequest 下载
                using (UnityWebRequest request = UnityWebRequest.Get(downloadUrl))
                {
                    request.timeout = 120;
                    // 跳过证书验证（解决打包版 TLS 连接问题）
                    request.certificateHandler = new BypassCertHandler();
                    
                    // 发送请求并等待完成
                    await request.SendWebRequest();

                    Debug.Log($"[Download] 响应码: {request.responseCode}");
                    Debug.Log($"[Download] 结果: {request.result}");
                    Debug.Log($"[Download] 错误: {request.error}");

                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        throw new Exception($"下载失败: {request.error} (HTTP {request.responseCode})");
                    }

                    byte[] data = request.downloadHandler.data;
                    Debug.Log($"[Download] 下载大小: {data.Length} bytes");

                    if (data == null || data.Length == 0)
                        throw new Exception($"谱面 {id} 不存在或下载失败");

                    // 保存并解密
                    await WriteChartZip(localPath, data);
                    downloadedCharts.Add(id);
                    Debug.Log($"[Download] 下载成功: {localPath}");

                    return data;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[Download] 异常: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
                throw;
            }
        }

        private static void CheckDirectory(string path)
        {
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
        }

        public static async Task<byte[]> DownloadFromPhiZone(string id)
        {
            return Array.Empty<byte>();
        }

        public static void OnRoomClosed()
        {
            downloadedCharts.Clear();
        }

        public static void OnRoomQuited()
        {
            chartMap.Clear();
            downloadedCharts.Clear();
            
            string onlineChartsDir = Path.Combine(TmpPathRoot, "online_charts");
            if (Directory.Exists(onlineChartsDir))
            {
                foreach (string file in Directory.GetFiles(onlineChartsDir))
                {
                    File.Delete(file);
                }
            }
        }

        private static async Task<byte[]> ReadChartZip(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            return TransformByteArray(bytes);
        }

        private static byte[] TransformByteArray(byte[] bytes)
        {
            byte[] result = new byte[bytes.Length];
            for (int i = 0; i < bytes.Length; i++)
            {
                result[i] = (byte)(bytes[i] ^ 0x4A);
            }
            return result;
        }

        private static async Task WriteChartZip(string path, byte[] bytes)
        {
            string dir = Path.GetDirectoryName(path);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            
            byte[] transformed = TransformByteArray(bytes);
            File.WriteAllBytes(path, transformed);
        }
    }
}