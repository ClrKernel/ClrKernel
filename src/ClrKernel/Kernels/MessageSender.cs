using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using ClrKernel.Protocols;
using NetMQ;
using Newtonsoft.Json;

namespace ClrKernel.Kernels;

public class MessageSender {
    private string _key;
    private NetMQSocket _iopub;

    // Display updates can arrive from background threads (timers, progress
    // loops); the multipart frame sequence must not interleave across sends.
    private readonly object _sendLock = new();

    public MessageSender(string key, NetMQSocket iopub) {
        _key = key;
        _iopub = iopub;
    }

    public bool Send<T, C>(Message<T> request, C content, string msgType) {
        lock (_sendLock) {
            return SendCore(request, content, msgType);
        }
    }

    private bool SendCore<T, C>(Message<T> request, C content, string msgType) {
        var ioPubMessage = new Message<C> {
            Identifiers = request.Identifiers,
            Delimiter = request.Delimiter,
            ParentHeader = request.Header,
            Header = new Header() {
                UserName = request.Header.UserName,
                Session = request.Header.Session,
                Date = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                MessageId = Guid.NewGuid().ToString(),
                MessageType = msgType,
                Version = request.Header.Version
            },
            Metadata = request.Metadata,
            Content = content
        };

        var encoder = new UTF8Encoding();
        List<string> messages = new List<string>();
        var signature = Sign(_key, ioPubMessage, messages, _iopub);

        // send
        foreach (var id in request.Identifiers) {
            _iopub.TrySendFrame(id, true);
        }

        _iopub.SendFrame(ioPubMessage.Delimiter, true);
        _iopub.SendFrame(signature, true);

        for (int i = 0; i < messages.Count; i++) {
            _iopub.SendFrame(messages[i], i < messages.Count - 1);
        }

        return true;
    }

    private string Sign<T>(string key, Message<T> ioPubMessage, List<string> messages, NetMQSocket iopub) {
        var encoder = new UTF8Encoding();
        var hMAC = new HMACSHA256(encoder.GetBytes(key));
        hMAC.Initialize();

        // https://jupyter-client.readthedocs.io/en/stable/messaging.html#the-wire-protocol

        messages.Add(JsonConvert.SerializeObject(ioPubMessage.Header));
        messages.Add(JsonConvert.SerializeObject(ioPubMessage.ParentHeader));
        messages.Add(JsonConvert.SerializeObject(ioPubMessage.Metadata));
        messages.Add(JsonConvert.SerializeObject(ioPubMessage.Content));

        // signature
        foreach (string item in messages) {
            var sourceBytes = encoder.GetBytes(item);
            hMAC.TransformBlock(sourceBytes, 0, sourceBytes.Length, null, 0);
        }

        hMAC.TransformFinalBlock(new byte[0], 0, 0);
        return BitConverter.ToString(hMAC.Hash).Replace("-", "").ToLower();
    }
}
