using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Raven.Server.Documents.PeriodicBackup.Aws;

namespace Raven.Server.Documents.PeriodicBackup.Retention
{
    public class S3RetentionPolicyRunner : RetentionPolicyRunnerBase
    {
        private readonly RavenAwsS3Client _client;

        protected override string Name => "S3";

        private const string Delimiter = "/";
        private string _folderContinuationToken = null;

        public S3RetentionPolicyRunner(RetentionPolicyBaseParameters parameters, RavenAwsS3Client client)
            : base(parameters)
        {
            _client = client;
        }

        protected override async Task<GetFoldersResult> GetSortedFolders()
        {
            var prefix = $"{_client.RemoteFolderName}{Delimiter}";
            var result = await _client.ListObjects(prefix, Delimiter, true, continuationToken: _folderContinuationToken);
            _folderContinuationToken = result.ContinuationToken;

            return new GetFoldersResult
            {
                List = result.FileInfoDetails.Select(x => x.FullPath).ToList(),
                HasMore = result.ContinuationToken != null
            };
        }

        protected override string GetFolderName(string folderPath)
        {
            return folderPath.Substring(0, folderPath.Length - 1);
        }

        protected override async Task<string> GetFirstFileInFolder(string folder)
        {
            // backups are ordered in lexicographical order
            var files = await _client.ListObjects(folder, null, false, 1);
            return files.FileInfoDetails?.Select(x => x.FullPath).FirstOrDefault();
        }

        protected override async Task DeleteFolders(List<string> folders)
        {
            // deleting multiple objects is limited to 1000 in a single batch
            const int numberOfObjectsInBatch = 1000;
            var objectsToDelete = new List<string>();

            foreach (var folder in folders)
            {
                string filesContinuationToken = null;

                do
                {
                    // delete all objects in that folder
                    var objects = await _client.ListObjects(folder, null, false, continuationToken: filesContinuationToken);

                    foreach (var file in objects.FileInfoDetails)
                    {
                        if (objectsToDelete.Count == numberOfObjectsInBatch)
                        {
                            await _client.DeleteMultipleObjects(objectsToDelete);
                            objectsToDelete.Clear();
                        }

                        objectsToDelete.Add(file.FullPath);
                    }

                    filesContinuationToken = objects.ContinuationToken;

                } while (filesContinuationToken != null);
            }

            if (objectsToDelete.Count > 0)
                await _client.DeleteMultipleObjects(objectsToDelete);
        }
    }
}