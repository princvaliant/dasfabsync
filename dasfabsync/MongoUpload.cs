using MongoDB.Bson;
using MongoDB.Driver;
using System;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using MongoDB.Driver.GridFS;
using System.Text;

namespace dasfabsync
{
    class MongoUpload
    {
        // Connection string to mongo production
        protected String connString = ConfigurationManager.AppSettings["mongoConnection"];
        // Database name in mongo db
        protected String dbName = ConfigurationManager.AppSettings["dbName"];
        // Collection name containing all sync locations/computers (see app.config file for more info)
        protected String syncLoc = ConfigurationManager.AppSettings["syncLocationCollection"];
        // Collection name containing all data files synced from various computers (see app.config file for more info)
        protected String syncFiles = ConfigurationManager.AppSettings["syncFilesCollection"];

        protected IMongoClient _client;
        protected IMongoDatabase _database;
        protected GridFSBucket _bucket;
        protected IMongoCollection<BsonDocument> _syncLoc;
        protected IMongoCollection<BsonDocument> _syncFiles;
        protected String _machineName;
        protected String _ipAddress;
        
        public MongoUpload()
        {
            // Initialize connections and private properties
            _client = new MongoClient(connString);
            _database = _client.GetDatabase(dbName);
            _bucket = new GridFSBucket(_database, new GridFSBucketOptions
            {
                BucketName = "files",
                ChunkSizeBytes = 1048576, // 1MB
                WriteConcern = WriteConcern.WMajority
            });
            _syncLoc = _database.GetCollection<BsonDocument>(syncLoc);
            _syncFiles = _database.GetCollection<BsonDocument>(syncFiles);
            _machineName = Environment.MachineName.ToUpper();
            foreach (IPAddress ip in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
            {
                if (ip.AddressFamily.ToString() == "InterNetwork")
                {
                    _ipAddress = ip.ToString();
                }
            }
        }
                
        // Call this function to start watching the path and upload data to mongodb
        public void start() {
            // Check if this machine is already registered 
            var filter = Builders<BsonDocument>.Filter.Eq("_id", _machineName);
            var result = _syncLoc.Find(filter).ToList();
            if (result.Count == 0)
            {
                // This is first run, register this computer
                var document = new BsonDocument {
                     { "_id", _machineName },
                     { "ipAddress", _ipAddress },
                     { "directories",  new BsonArray()},
                     { "createDate", DateTime.UtcNow  }
                };
                _syncLoc.InsertOne(document);
            }
            else
            {
                // Process directory list defined in admin app
                processDirectories(filter, result[0]);
            }
        }

        protected void processDirectories(FilterDefinition<BsonDocument> filter, BsonDocument syncDoc)  {
            foreach(BsonDocument dir in (BsonArray)syncDoc.GetValue("directories"))
            {
                try {
                    // Mark time started so we can update lastRun
                    DateTime startTime = DateTime.UtcNow;
                    // Upload all matching files to mongodb
                    uploadFilesToMongo(dir);
                    // Set lastRun to enable incremental upload
                    dir.Set("lastRun", startTime);
                    // If there was error previously, clear it
                    dir.Remove("error");
                }
                catch (Exception exc) {
                    dir.Set("error", new BsonDocument {
                         { "msg", exc.Message },
                         { "src", exc.Source },
                         { "type", exc.GetType().ToString() }
                    });
                }
            }
            // Update document to reflect all changes
            _syncLoc.ReplaceOne(filter, syncDoc);
        }

        protected void uploadFilesToMongo(BsonDocument dirDoc)
        {
            String dirPath = dirDoc.GetValue("path").ToString();
            String fileFilter = dirDoc.GetValue("fileFilter").ToString();
            // Replace forward slash with double backslash on windows
            dirPath = dirPath.Replace("/", "\\");
            // Throw exception if path not found
            if (!Directory.Exists(dirPath))
            {
                throw new FileNotFoundException();
            } 
            var directory = new DirectoryInfo(dirPath);
            DateTime lastRun = getLastRun(dirDoc);
            // Upload first files in root of the directory
            uploadFileData(dirDoc, "", directory.GetFiles()
                .Where(file => file.CreationTime >= lastRun)
                .Where(file => Regex.Match(file.Name, fileFilter).Success));
            // Start recursing through subdirectories
            recurseDir(dirDoc, dirPath, fileFilter, lastRun);
        }

        protected void recurseDir(BsonDocument dirDoc, string sDir, String filter, DateTime lastRun)
        {
            foreach (string dir in Directory.GetDirectories(sDir))
            {
                var directory = new DirectoryInfo(dir);
                uploadFileData(dirDoc, directory.Name, directory.GetFiles()
               .Where(file => file.LastWriteTime >= lastRun)
               .Where(file => Regex.Match(file.Name, filter).Success));
                recurseDir(dirDoc, dir, filter, lastRun);
            }     
        }

        protected DateTime getLastRun(BsonDocument dirDoc)
        {
            if (dirDoc.Contains("lastRun"))
            {
                return dirDoc.GetValue("lastRun").ToLocalTime();
            }
            else
            {
                return DateTime.Now.AddYears(-100);
            };
        }

        protected void uploadFileData(BsonDocument dirDoc, String subPath, IEnumerable<FileInfo> files)
        {
            string[] imageExtensions = { "png", "jpg", "jpeg", "gif" };
            Regex regex = null;
            if (dirDoc.GetValue("codeRegex") != null) {
                regex = new Regex(dirDoc.GetValue("codeRegex").ToString());
            }

            foreach (FileInfo file in files)
            {
                String content;
                String extension = file.Extension.Replace(".", "").ToLower();

                if (imageExtensions.Contains(extension)) { 
                    byte[] imageArray = System.IO.File.ReadAllBytes(file.FullName);
                    content = Convert.ToBase64String(imageArray);
                    content = "data:image/" + extension + ";base64," + content;
                } else
                {
                    StreamReader sr = new StreamReader(file.FullName, Encoding.UTF8);
                    content = sr.ReadToEnd();
                    sr.Close();
                }

                String code = "";
                if (regex != null)
                {
                    var v = regex.Match(file.Name);
                    code = v.Groups[1].ToString();
                }

                var query = new BsonDocument {
                     { "source", _machineName },
                     { "path", dirDoc.GetValue("path").ToString() },
                     { "subpath", subPath },
                     { "fileName", file.Name }
                };

                var doc = new BsonDocument {
                     { "source", _machineName },
                     { "path", dirDoc.GetValue("path").ToString() },
                     { "subpath", subPath },
                     { "fileName", file.Name },
                     { "fileType", file.Extension},
                     { "code", code },
                     { "content", content},
                     { "length", content.Length},
                     { "processed", false},
                     { "dateCreated",  file.LastWriteTime },
                     { "dateProcessed", "" }
                };
                if (_syncFiles.Count(query) == 0) {
                    _syncFiles.InsertOne(doc);
                } else
                {
                    _syncFiles.ReplaceOne(query, doc);
                }
            }
        }
    }
}
