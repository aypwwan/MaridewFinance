using System.Runtime.CompilerServices;

// Lets MaridewFinance.Tests exercise the internal sync logic (fingerprints,
// merges, tombstones, blob codec, KDF) without making it public API.
[assembly: InternalsVisibleTo("MaridewFinance.Tests")]
