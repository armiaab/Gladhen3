using Xunit;

// Tests mutate the static AppSettings.Current via ReplaceForTesting. Run them serially so
// one class can't change the active settings while another is mid-conversion (which would
// otherwise make the file-size assertions in PdfCompressionTests flaky).
[assembly: CollectionBehavior(DisableTestParallelization = true)]
