using Azure;
using Azure.Storage.Queues;

// Local agent: reads commands the dashboard queued (pause, answer, limit, ...) and will
// post session state back. The Claude sessions themselves run on this machine.
var connection = Environment.GetEnvironmentVariable("ORC_QUEUE_CONNECTION")
    ?? throw new InvalidOperationException("Set ORC_QUEUE_CONNECTION to the Storage Queue connection string.");

var commands = new QueueClient(connection, "orc-commands");
await commands.CreateIfNotExistsAsync();

Console.WriteLine("orc agent: listening on queue 'orc-commands'");
while (true)
{
    Response<QueueMessage[]> batch = await commands.ReceiveMessagesAsync(maxMessages: 10, visibilityTimeout: TimeSpan.FromMinutes(1));
    foreach (var message in batch.Value)
    {
        // TODO: apply the command to the local panel/worker logic, then delete it.
        Console.WriteLine($"command: {message.MessageText}");
        await commands.DeleteMessageAsync(message.MessageId, message.PopReceipt);
    }
    if (batch.Value.Length == 0) await Task.Delay(TimeSpan.FromSeconds(5));
}
