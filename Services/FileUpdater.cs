using SunriseLauncher.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SunriseLauncher.Services
{
    public class FileUpdater
    {
        private readonly SemaphoreSlim semaphore = new SemaphoreSlim(1);
        private HttpClient client = new HttpClient();

        public async Task<UpdateResult> UpdateAsync(Server server, bool force)
        {
            if (server.State == State.Updating)
                return new UpdateResult(true, null);

            try
            {
                server.State = State.Updating;
                server.ProgressDesc = "retrieving manfiest";
                server.ProgressValue = 0;
                server.ProgressMax = 0;
                server.CancellationTokenSource = new CancellationTokenSource();

                var manifest = MainfestFactory.Get(server.ManifestURL);
                if (manifest == null)
                {
                    Console.WriteLine("Unknown manifest schema at '{0}'", server.ManifestURL);
                    server.State = State.Ready;
                    return new UpdateResult(false, "Unknown manifest schema. You may still attempt to play, but you may be missing updates.");
                }

                var metadata = await manifest.GetMetadataAsync();
                if (metadata == null)
                {
                    Console.WriteLine("Could not retrieve manifest from '{0}'", server.ManifestURL);
                    server.State = State.Ready;
                    return new UpdateResult(false, "Could not retrieve manifest. You may still attempt to play, but you may be missing updates.");
                }

                if (!metadata.Verify())
                {
                    Console.WriteLine("Manifest metadata failed inspection '{0}'", server.ManifestURL);
                    server.State = State.Ready;
                    return new UpdateResult(false, "Manifest failed inspection. You may still attempt to play, but you may be missing updates.");
                }

                if (force || metadata.Version != server.Metadata.Version)
                {
                    var result = await Updatefiles(server, manifest, server.InstallPath);
                    if (!result.Success) return result;
                }
                else
                {
                    server.State = State.Ready;
                }

                if (server.State == State.Ready)
                {
                    server.Metadata = metadata;
                }

                //if update occurs which removes the selected launch option, default to first option available
                if (metadata.LaunchOptions.All(x => x.Name != server.Launch))
                {
                    server.Launch = metadata.LaunchOptions[0].Name;
                }
            }
            catch (Exception ex)
            {
                server.State = State.Error;
                Console.WriteLine("exception in UpdateAsync: {0}", ex.Message);
                if (ex.StackTrace != null)
                    Console.WriteLine(ex.StackTrace);
                return new UpdateResult(false, "UpdateAsync Exception");
            }
            finally
            {
                server.ProgressDesc = null;
                server.ProgressValue = 0;
                server.ProgressMax = 0;
            }
            return new UpdateResult(true, null);
        }

        private async Task<UpdateResult> Updatefiles(Server server, IManifest manifest, string path)
        {
            server.ProgressDesc = "waiting in queue ...";
            await semaphore.WaitAsync();
            try
            {
                var token = server.CancellationTokenSource.Token;

                var files = await manifest.GetFilesAsync();
                if (files == null)
                {
                    server.State = State.Error;
                    return new UpdateResult(false, "Could not retrieve files from manifest.");
                }

                server.ProgressMax = files.Count;
                foreach (var file in files)
                {
                    if (token.IsCancellationRequested)
                    {
                        server.State = State.Error;
                        return new UpdateResult(false, null);
                    }

                    server.ProgressValue++;

                    if (!file.Verify())
                    {
                        server.State = State.Error;
                        return new UpdateResult(false, "Manifest file failed inspection " + file.Path);
                    }

                    var result = await Updatefile(file, server);
                    if (!result.Success)
                    {
                        server.State = State.Error;
                        return result;
                    }
                }
            }
            catch (Exception ex)
            {
                server.State = State.Error;
                Console.WriteLine(string.Format("exception in updatefiles: {0}", ex.Message));
                return new UpdateResult(false, "UpdateFiles Exception");
            }
            finally
            {
                semaphore.Release();
                server.ProgressDesc = null;
            }

            server.State = State.Ready;
            return new UpdateResult(true, null);
        }

        private async Task<UpdateResult> Updatefile(ManifestFile file, Server server)
        {
            if (await Checkfile(file, server))
                return new UpdateResult(true, null);

            var path = Path.Combine(server.InstallPath, file.Path);
            var tempfile = path + "~";
            var token = server.CancellationTokenSource.Token;

            Console.WriteLine("downloading {0}", path);
            Shuffler.Shuffle(file.Sources);
            foreach (var source in file.Sources)
            {
                Console.WriteLine("downloading from source '{0}'.", source.URL);
                try
                {
                    server.ProgressDesc = "downloading " + file.Path;
                    server.ProgressMaxFile = file.Size;

                    using (var hash = Hashing.GetHashAlgorithm(file))
                    {
                        if (hash == null)
                            return new UpdateResult(false, "hash algorithm missing for " + file.Path);

                        var dirname = Path.GetDirectoryName(path);
                        if (!string.IsNullOrWhiteSpace(dirname)) Directory.CreateDirectory(dirname);

                        long size = 0;
                        byte[] checksum;
                        var response = await client.GetAsync(source.URL, HttpCompletionOption.ResponseHeadersRead);
                        if (response.IsSuccessStatusCode)
                        {
                            using (var filestream = new FileStream(tempfile, FileMode.Create))
                            using (var hashstream = new CryptoStream(filestream, hash, CryptoStreamMode.Write))
                            using (var reader = await response.Content.ReadAsStreamAsync())
                            {
                                size = await CopyToProgressFileAsync(reader, hashstream, 81920, server, token);
                                hashstream.FlushFinalBlock();
                                checksum = hash.Hash;
                            }

                            if (size == file.Size && Hashing.VerifyChecksum(checksum, file))
                            {
                                File.Move(tempfile, path, true);
                                return new UpdateResult(true, null);
                            }
                            else
                            {
                                Console.WriteLine("size or hash did not match manifest from source {0}", source.URL);
                                File.Delete(tempfile);
                            }
                        }
                        else
                        {
                            Console.WriteLine("cannot get file from source {0}", source.URL);
                        }
                    }
                }
                catch (OperationCanceledException ex)
                {
                    if (ex.CancellationToken == token)
                    {
                        Console.WriteLine("update stopped due to cancellation request");
                        return new UpdateResult(false, "");
                    }

                    Console.WriteLine("OperationCanceledException while downloading source {0}: {1}", source.URL, ex.Message);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("exception while downloading source {0}: {1}", source.URL, ex.Message);
                    if (ex.InnerException != null)
                    {
                        Console.WriteLine("inner exception: {0}", ex.InnerException.Message);
                    }
                }
                finally
                {
                    server.ProgressDesc = null;
                    server.ProgressValueFile = 0;
                    server.ProgressMaxFile = 0;
                }
            }
            return new UpdateResult(false, "Could not update file " + file.Path);
        }

        private async Task<bool> Checkfile(ManifestFile file, Server server)
        {
            var path = Path.Combine(server.InstallPath, file.Path);
            Console.WriteLine("checking {0}", path);

            if (!File.Exists(path))
            {
                return file.Size == 0;
            }
            else if (file.Size == 0)
            {
                File.Delete(path);
                return true;
            }

            using (var hash = Hashing.GetHashAlgorithm(file))
            {
                if (hash == null) return false;

                byte[] checksum;
                long size = 0;
                try
                {
                    server.ProgressDesc = "verifying " + file.Path;
                    server.ProgressMaxFile = file.Size;

                    using (FileStream filestream = new FileStream(path, FileMode.Open))
                    using (var hashstream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write))
                    {
                        size = await CopyToProgressFileAsync(filestream, hashstream, 81920, server, CancellationToken.None);
                        hashstream.FlushFinalBlock();
                        checksum = hash.Hash;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("exception while verifying {0}: {1}", file.Path, ex.Message);
                    return false;
                }

                return (size == file.Size && Hashing.VerifyChecksum(checksum, file));
            }
        }

        private async Task<long> CopyToProgressFileAsync(Stream fromStream, Stream destination, int bufferSize, Server server, CancellationToken cancellationToken)
        {
            server.ProgressValueFile = 0;
            var buffer = new byte[bufferSize];
            long size = 0;
            int count;
            while ((count = await fromStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) != 0)
            {
                size += count;
                server.ProgressValueFile = size;
                await destination.WriteAsync(buffer, 0, count, cancellationToken);
            }
            server.ProgressValueFile = 0;
            server.ProgressMaxFile = 0;
            return size;
        }
    }

    public struct UpdateResult
    {
        public bool Success;
        public string Message;

        public UpdateResult(bool success, string message)
        {
            Success = success;
            Message = message;
        }
    }
}
