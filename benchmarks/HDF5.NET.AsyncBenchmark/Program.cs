using System;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Threading.Tasks.Schedulers;
using HDF5.NET;

const ulong CHUNK_SIZE = 1 * 1024 * 256; // 4 bytes per value
const ulong CHUNK_COUNT = 1000;

const ulong SEGMENT_COUNT = 10;
const ulong BUFFER_SIZE = CHUNK_SIZE * SEGMENT_COUNT;
const ulong BUFFER_BYTE_SIZE = BUFFER_SIZE * sizeof(float);
const ulong COUNT = CHUNK_COUNT / SEGMENT_COUNT;

var syncFilePath = "/tmp/HDF5.NET/sync.h5";
var asyncFilePath = "/tmp/HDF5.NET/async.h5";

try
{
    // 1. create files
    foreach (var filePath in new string[] { syncFilePath, asyncFilePath })
    {
        if (!File.Exists(filePath))
        {
            Console.WriteLine($"Create test file {filePath}.");

            var process = new Process();
            process.StartInfo.FileName = "python";
            process.StartInfo.Arguments = $"benchmarks/HDF5.NET.AsyncBenchmark/create_test_file.py {filePath}";
            process.Start();
            process.WaitForExit();

            var exitCode = process.ExitCode;

            if (exitCode != 0)
                throw new Exception("Unable to create test files.");
        }

        else
        {
            Console.WriteLine($"Test file {filePath} already exists.");
        }
    }

    // 2. ask user to clear cache
    // https://medium.com/marionete/linux-disk-cache-was-always-there-741bef097e7f
    // https://unix.stackexchange.com/a/82164
    Console.WriteLine("Please run the following command to clear the file cache and monitor the cache usage:");
    Console.WriteLine("free -wh && sync && echo 1 | sudo sysctl vm.drop_caches=1 && free -wh");
    Console.WriteLine();
    Console.WriteLine("Press any key to continue ...");

    Console.ReadKey(intercept: true);

    // 3. sync test
    var syncResult = 0.0f;

    using var file_sync = H5File.Open(
        syncFilePath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        useAsync: false
    );

    var dataset_sync = file_sync.Dataset("chunked");

    Console.WriteLine($"Run sync test.");

    var syncBuffer = Enumerable.Range(0, (int)BUFFER_SIZE).Select(value => (float)value).ToArray();
    var stopwatch_sync = Stopwatch.StartNew();

    for (uint i = 0; i < COUNT; i++)
    {
        var fileSelection = new HyperslabSelection(
            start: i * BUFFER_SIZE,
            block: BUFFER_SIZE
        );

        dataset_sync.Read<float>(syncBuffer, fileSelection);

        syncResult += ProcessData(syncBuffer);
    }

    var elapsed_sync = stopwatch_sync.Elapsed;
    Console.WriteLine($"The sync test took {elapsed_sync.TotalMilliseconds:F1} ms. The result is {syncResult}.");

    // 4. async test
    var asyncResult = 0.0f;

    using var file_async = H5File.Open(
        asyncFilePath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        useAsync: true
    );

    var dataset_async = file_async.Dataset("chunked");
    var scheduler = new LimitedConcurrencyLevelTaskScheduler(maxDegreeOfParallelism: 1);
    var threadId = Thread.CurrentThread.ManagedThreadId;

    Console.WriteLine($"Run async test.");

    var stopwatch_async = Stopwatch.StartNew();

    var options = new PipeOptions(
        useSynchronizationContext: false);

    var pipe = new Pipe(options);
    var reader = pipe.Reader;
    var writer = pipe.Writer;

    var reading = Task.Factory.StartNew(async () =>
    {
        for (uint i = 0; i < COUNT; i++)
        {
            var asyncBuffer = new CastMemoryManager<byte, float>(writer.GetMemory((int)BUFFER_BYTE_SIZE)).Memory;

            var fileSelection = new HyperslabSelection(
                start: i * BUFFER_SIZE,
                block: BUFFER_SIZE
            );

            await dataset_async.ReadAsync<float>(asyncBuffer, fileSelection);

            writer.Advance((int)BUFFER_BYTE_SIZE);
            await writer.FlushAsync();
        }

        await writer.CompleteAsync();
    }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, scheduler).Unwrap();

    var processingTime = TimeSpan.Zero;

    var processing = Task.Factory.StartNew(async () =>
    {
        while (true)
        {
            var result = await reader.ReadAsync();

            if (result.IsCompleted)
                break;

            var asyncBuffer = result.Buffer.First;
            var processingTimeSw = Stopwatch.StartNew();

            asyncResult += ProcessData(MemoryMarshal.Cast<byte, float>(asyncBuffer.Span));
            processingTime += processingTimeSw.Elapsed;
            reader.AdvanceTo(result.Buffer.GetPosition((long)BUFFER_BYTE_SIZE));
        }

        await reader.CompleteAsync();
    }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, scheduler).Unwrap();

    await Task.WhenAll(reading, processing);

    var elapsed_async = stopwatch_async.Elapsed;
    Console.WriteLine($"The async test took {elapsed_async.TotalMilliseconds:F1} ms. The result is {asyncResult}.");
    Console.WriteLine($"The pure processing time was {processingTime.TotalMilliseconds:F1} ms.");

    //
    Console.WriteLine($"The different sync - async is {(elapsed_sync - elapsed_async).TotalMilliseconds:F1} ms.");
}
finally
{
    // if (File.Exists(syncFilePath))
    // {
    //     try { File.Delete(syncFilePath); }
    //     catch { }
    // }

    // if (File.Exists(asyncFilePath))
    // {
    //     try { File.Delete(asyncFilePath); }
    //     catch { }
    // }
}

float ProcessData(ReadOnlySpan<float> data)
{
    var sum = 0.0f;

    for (int i = 0; i < data.Length; i++)
    {
        sum += data[i];
    }

    for (int i = 0; i < data.Length; i++)
    {
        sum += data[i];
    }

    for (int i = 0; i < data.Length; i++)
    {
        sum += data[i];
    }

    return sum;
}

internal class CastMemoryManager<TFrom, TTo> : MemoryManager<TTo>
            where TFrom : struct
            where TTo : struct
{
    private readonly Memory<TFrom> _from;

    public CastMemoryManager(Memory<TFrom> from) => _from = from;

    public override Span<TTo> GetSpan() => MemoryMarshal.Cast<TFrom, TTo>(_from.Span);

    protected override void Dispose(bool disposing)
    {
        //
    }

    public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();

    public override void Unpin() => throw new NotSupportedException();
}