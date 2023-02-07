﻿using Amazon.SQS.Model;
namespace MinimalQueues.AwsSqs;

internal class MessageGroup: IAsyncDisposable
{
    private readonly AwsSqsConnection _connection;
    private          HashSet<Message> _messages;
    private readonly PeriodicTimer    _timer;
    private readonly Task             _updateVisibilityTask;

    public MessageGroup(AwsSqsConnection connection)
    {
        _connection = connection;
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(_connection.RenewVisibilityWaitTime));
        _updateVisibilityTask = UpdateVisibility();
    }
    public IEnumerable<PrefetchedSqsMessage> Initialize(List<Message>? messages)
    {
        if (messages is null) yield break;
        _messages = messages.ToHashSet();
        for (int i = 0; i < messages.Count; i++)
        {
            yield return new PrefetchedSqsMessage(this, messages[i]);
        }
    }
    private async Task UpdateVisibility()
    {
        var updatevisibilityTask = Task.CompletedTask;
        while (await _timer.WaitForNextTickAsync())
        {
            await updatevisibilityTask;
            var requestEntries = GetRequestsEntries();
            if (requestEntries.Count is 0) continue;
            var request = new ChangeMessageVisibilityBatchRequest(_connection.QueueUrl, requestEntries);
            updatevisibilityTask = _connection._sqsClient.ChangeMessageVisibilityBatchAsync(request);
        }
    }

    private List<ChangeMessageVisibilityBatchRequestEntry> GetRequestsEntries()
    {
        lock (this)
        {
            return _messages.Select(m => new ChangeMessageVisibilityBatchRequestEntry(m.ReceiptHandle, m.ReceiptHandle)).ToList(); 
        }
    }

    private ValueTask Remove(Message message)
    {
        bool dispose = false;
        lock (this)
        {
            _messages.Remove(message);
            dispose = _messages.Count is 0;
        }
        if (dispose)
        {
            return DisposeAsync();
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _timer.Dispose();
        return new ValueTask(_updateVisibilityTask);
    }

    internal class PrefetchedSqsMessage : SqsMessage
    {
        private readonly MessageGroup _messageGroup;

        public PrefetchedSqsMessage(MessageGroup messageGroup, Message internalMessage)
            :base(internalMessage)
        {
            _messageGroup = messageGroup;
        }
        public override BinaryData GetBody()
        {
            return new BinaryData(InternalMessage.Body);
        }
        public override ValueTask DisposeAsync()
        {
            return _messageGroup.Remove(InternalMessage);
        }
    }
}