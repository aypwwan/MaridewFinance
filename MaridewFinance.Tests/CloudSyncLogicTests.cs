using System.Text.Json.Nodes;
using MaridewFinance.App;
using Xunit;

namespace MaridewFinance.Tests
{
    /// <summary>
    /// Tests the desktop sync service's pure logic - the same rules that
    /// keep web/Android/desktop data converging: fingerprint-based no-churn
    /// push detection, union merges with cloud/local precedence, tombstone
    /// recording and pruning, and the zero-knowledge blob format.
    /// </summary>
    public class CloudSyncLogicTests
    {
        [Fact]
        public void Fingerprint_IsStableAcrossInstances()
        {
            var a = new JsonObject { ["tables"] = new JsonObject { ["transactions"] = new JsonArray { 1, 2 } } };
            var b = new JsonObject { ["tables"] = new JsonObject { ["transactions"] = new JsonArray { 1, 2 } } };
            Assert.Equal(CloudSyncService.Fingerprint(a), CloudSyncService.Fingerprint(b));
        }

        [Fact]
        public void Fingerprint_ChangesWhenPayloadChanges()
        {
            var a = new JsonObject { ["tables"] = new JsonObject { ["transactions"] = new JsonArray { 1 } } };
            var b = new JsonObject { ["tables"] = new JsonObject { ["transactions"] = new JsonArray { 2 } } };
            Assert.NotEqual(CloudSyncService.Fingerprint(a), CloudSyncService.Fingerprint(b));
        }

        [Fact]
        public void MergeRows_CloudNewerPrefersCloudValues()
        {
            var local = new JsonArray { Row(1, "local") };
            var cloud = new JsonArray { Row(1, "cloud") };
            var merged = CloudSyncService.MergeRows(local, cloud, cloudNewer: true);
            Assert.Single(merged);
            Assert.Equal("cloud", merged[0]!["value"]!.GetValue<string>());
        }

        [Fact]
        public void MergeRows_UnionKeepsLocalOnlyRows()
        {
            var local = new JsonArray { Row(1, "a"), Row(2, "b") };
            var cloud = new JsonArray { Row(1, "a") };
            var merged = CloudSyncService.MergeRows(local, cloud, cloudNewer: false);
            Assert.Equal(2, merged.Count);
        }

        [Fact]
        public void MergeRows_DeletedIdsNeverResurrect()
        {
            // id 5 was deleted on this device; id 6 is new from the cloud.
            // The merge must carry 6 but never bring 5 back.
            var local = new JsonArray { Row(1, "a") };
            var cloud = new JsonArray { Row(1, "a"), Row(5, "ghost"), Row(6, "new") };
            var merged = CloudSyncService.MergeRows(local, cloud, cloudNewer: true, deletedIds: new long[] { 5 });
            Assert.NotNull(merged);
            Assert.Equal(2, merged.Count);
            Assert.DoesNotContain(merged, r => r!["id"]!.GetValue<long>() == 5);
            Assert.Contains(merged, r => r!["id"]!.GetValue<long>() == 6);
        }

        [Fact]
        public void MergeRows_DeletedCloudConvergesToNoChange()
        {
            // A cloud whose only extra rows are deleted ones converges to
            // "no write needed" (null) - the tombstone already won.
            var local = new JsonArray { Row(1, "a") };
            var cloud = new JsonArray { Row(1, "a"), Row(5, "ghost") };
            Assert.Null(CloudSyncService.MergeRows(local, cloud, cloudNewer: true, deletedIds: new long[] { 5 }));
        }

        [Fact]
        public void MergeRows_IdenticalSidesProduceNull()
        {
            var rows = new JsonArray { Row(1, "same") };
            Assert.Null(CloudSyncService.MergeRows(rows, (JsonArray)rows.DeepClone(), cloudNewer: true));
        }

        // ------------------------------------------------------------ tombstones

        [Fact]
        public void Tombstones_RoundTripThroughJson()
        {
            var tombs = new Dictionary<string, Dictionary<long, string>>();
            CloudSyncService.RecordTombstone(tombs, "transactions", 42, "2026-09-25T00:00:00Z");
            var json = CloudSyncService.TombstonesToJson(tombs);
            var back = CloudSyncService.TombstonesFromJson(json);
            Assert.True(back["transactions"].ContainsKey(42));
            Assert.Equal("2026-09-25T00:00:00Z", back["transactions"][42]);
        }

        [Fact]
        public void Tombstones_FromPayloadExtractsDeletionLog()
        {
            var payload = new JsonObject
            {
                ["tables"] = new JsonObject(),
                ["tombstones"] = new JsonObject { ["loans"] = new JsonObject { ["7"] = "2026-01-01T00:00:00Z" } }
            };
            var tombs = CloudSyncService.TombstonesFromPayload(payload);
            Assert.True(tombs["loans"].ContainsKey(7));
        }

        [Fact]
        public void Tombstones_PruneDropsOldEntries()
        {
            var tombs = new Dictionary<string, Dictionary<long, string>>
            {
                ["transactions"] = new Dictionary<long, string>
                {
                    [1] = DateTime.UtcNow.AddDays(-60).ToString("o"),
                    [2] = DateTime.UtcNow.AddHours(-1).ToString("o"),
                }
            };
            // PruneTombstones mutates in place and returns the remaining count.
            var remaining = CloudSyncService.PruneTombstones(tombs, maxAgeDays: 30);
            Assert.False(tombs["transactions"].ContainsKey(1));
            Assert.True(tombs["transactions"].ContainsKey(2));
            Assert.Equal(1, remaining);
        }

        [Fact]
        public void RecordTombstone_KeepsLatestTimestamp()
        {
            var tombs = new Dictionary<string, Dictionary<long, string>>();
            CloudSyncService.RecordTombstone(tombs, "t", 1, "2026-01-01T00:00:00Z");
            CloudSyncService.RecordTombstone(tombs, "t", 1, "2026-06-01T00:00:00Z");
            CloudSyncService.RecordTombstone(tombs, "t", 1, "2025-01-01T00:00:00Z");
            Assert.Equal("2026-06-01T00:00:00Z", tombs["t"][1]);
        }

        // ------------------------------------------------- zero-knowledge blobs

        [Fact]
        public void Blob_EncodeDecodeRoundTrips()
        {
            var payload = new JsonObject { ["tables"] = new JsonObject { ["transactions"] = new JsonArray { Row(9, "x") } } };
            var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            var blob = CloudSyncService.EncodeBlob(payload, key);
            var decoded = CloudSyncService.DecodeBlob(blob, key);
            Assert.NotNull(decoded);
            Assert.Equal(payload.ToJsonString(), decoded!.ToJsonString());
        }

        [Fact]
        public void Blob_RejectsWrongKey()
        {
            var payload = new JsonObject { ["tables"] = new JsonObject() };
            var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            var other = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            var blob = CloudSyncService.EncodeBlob(payload, key);
            Assert.Null(CloudSyncService.DecodeBlob(blob, other));
        }

        [Fact]
        public void Blob_EncryptsToServerOnlySeesCiphertext()
        {
            var payload = new JsonObject { ["tables"] = new JsonObject() };
            var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            var blob = CloudSyncService.EncodeBlob(payload, key);
            Assert.DoesNotContain("tables", blob.Split(':')[1]);   // no plaintext marker survives
            Assert.True(blob.Contains(':'), "ivB64:ctB64 format preserved");
        }

        // ------------------------------------------------------ password KDF

        [Fact]
        public void DeriveAuthHex_IsDeterministicPerUserAndPassword()
        {
            var a1 = CloudSyncService.DeriveAuthHex("jaman", "hunter22");
            var a2 = CloudSyncService.DeriveAuthHex("jaman", "hunter22");
            var b = CloudSyncService.DeriveAuthHex("jaman", "hunter23");
            var other = CloudSyncService.DeriveAuthHex("other", "hunter22");
            Assert.Equal(a1, a2);
            Assert.NotEqual(a1, b);
            Assert.NotEqual(a1, other);
        }

        [Fact]
        public void DeriveEncKey_MatchesBetweenAuthAndEncSalts()
        {
            var enc = CloudSyncService.DeriveEncKey("jaman", "hunter22");
            Assert.Equal(32, enc.Length);   // AES-256
            var auth = CloudSyncService.DeriveAuthHex("jaman", "hunter22");
            Assert.NotEqual(Convert.ToHexString(enc).ToLowerInvariant(), auth);
        }

        private static JsonNode Row(long id, string value) =>
            new JsonObject { ["id"] = id, ["value"] = value };
    }

    /// <summary>Version-compare rules for the auto-update feed.</summary>
    public class UpdateServiceTests
    {
        [Theory]
        [InlineData("1.0.5", "1.0.6", -1)]
        [InlineData("1.0.6", "1.0.5", 1)]
        [InlineData("1.0.5", "1.0.5", 0)]
        [InlineData("1.10.0", "1.9.9", 1)]
        [InlineData("2.0.0", "1.99.99", 1)]
        public void CompareVersions_OrdersSemverLike(string a, string b, int expectedSign)
        {
            var cmp = UpdateService.CompareVersions(a, b);
            Assert.Equal(Math.Sign(expectedSign), Math.Sign(cmp));
        }
    }
}
