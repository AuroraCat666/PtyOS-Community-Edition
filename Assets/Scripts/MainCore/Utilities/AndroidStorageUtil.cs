using System;

namespace MainCore.Utilities
{
    public static class AndroidStorageUtil
    {
        private const string ExternalStorageAuthority = "com.android.externalstorage.documents";
        private const string DownloadsAuthority = "com.android.providers.downloads.documents";

        public static string SAFUriToRealPath(string path)
        {
            if (string.IsNullOrEmpty(path) || !path.StartsWith("content://", StringComparison.Ordinal)) return path;

            if (path.StartsWith("content://" + ExternalStorageAuthority, StringComparison.Ordinal))
            {
                string docId = null;
                int docIdx = path.LastIndexOf("/document/", StringComparison.Ordinal);
                if (docIdx >= 0)
                {
                    docId = path.Substring(docIdx + "/document/".Length);
                }
                else
                {
                    int treeIdx = path.IndexOf("/tree/", StringComparison.Ordinal);
                    if (treeIdx >= 0) docId = path.Substring(treeIdx + "/tree/".Length);
                }

                if (string.IsNullOrEmpty(docId)) return path;

                docId = Uri.UnescapeDataString(docId);
                int colonIndex = docId.IndexOf(':');
                if (colonIndex < 0) return path;

                string volume = docId.Substring(0, colonIndex);
                string relative = docId.Substring(colonIndex + 1);
                string basePath = volume == "primary" ? "/storage/emulated/0" : "/storage/" + volume;

                return string.IsNullOrEmpty(relative) ? basePath : basePath + "/" + relative;
            }

            if (path.StartsWith("content://" + DownloadsAuthority + "/", StringComparison.Ordinal))
            {
                return "/storage/emulated/0/Download";
            }

            return path;
        }
    }
}