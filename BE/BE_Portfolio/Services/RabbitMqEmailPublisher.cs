using System.Text;
using System.Text.Json;
using BE_Portfolio.Models.Commons;
using BE_Portfolio.Models.Specification;
using BE_Portfolio.Services.Interfaces;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace BE_Portfolio.Services;

public sealed class RabbitMqEmailPublisher : IEmailQueue, IAsyncDisposable
{
    private readonly RabbitMqSettings _cfg;
    private readonly ILogger<RabbitMqEmailPublisher> _logger;
    private readonly ConnectionFactory _factory;
    private IConnection? _conn;
    private IChannel? _ch;

    public RabbitMqEmailPublisher(IOptions<RabbitMqSettings> opts, ILogger<RabbitMqEmailPublisher> logger)
    {
        _cfg = opts.Value;
        _logger = logger;
        
        _factory = new ConnectionFactory {
            AutomaticRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(5)
        };

        if (!string.IsNullOrEmpty(_cfg.Uri))
        {
            _factory.Uri = new Uri(_cfg.Uri);
        }
        else
        {
            _factory.HostName = _cfg.HostName;
            _factory.Port = 5672;
            _factory.UserName = _cfg.UserName;
            _factory.Password = _cfg.Password;
            _factory.VirtualHost = "/";
        }
    }

    private async Task EnsureChannelAsync(CancellationToken ct)
    {
        _conn ??= await _factory.CreateConnectionAsync(ct).ConfigureAwait(false);
        _ch   ??= await _conn.CreateChannelAsync(cancellationToken: ct).ConfigureAwait(false);

        await _ch.QueueDeclareAsync(
            queue: _cfg.QueueName,
            durable: true, exclusive: false, autoDelete: false,
            arguments: null, cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task EnqueueAsync(EmailMessage msg, CancellationToken ct = default)
    {
        await EnsureChannelAsync(ct);

        var json = JsonSerializer.Serialize(msg);
        var body = Encoding.UTF8.GetBytes(json);

        // publish trực tiếp vào queue mặc định (exchange "")
        await _ch!.BasicPublishAsync(
            exchange: "",
            routingKey: _cfg.QueueName,
            mandatory: false,
            body: body,
            cancellationToken: ct).ConfigureAwait(false);

        _logger.LogInformation("Enqueued email to {To}", msg.ToEmail);
    }

    public async ValueTask DisposeAsync()
    {
        if (_ch != null) { await _ch.CloseAsync(); _ch.Dispose(); }
        if (_conn != null) { await _conn.CloseAsync(); _conn.Dispose(); }
    }
}