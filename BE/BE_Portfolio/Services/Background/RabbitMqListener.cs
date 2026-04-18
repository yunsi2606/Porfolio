using System.Text;
using System.Text.Json;
using BE_Portfolio.Models.Commons;
using BE_Portfolio.Models.Specification;
using BE_Portfolio.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace BE_Portfolio.Services.Background;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

public class RabbitMqListener : BackgroundService
{
    private readonly ConnectionFactory _factory;
    private IConnection? _connection;
    private IChannel? _channel;
    private readonly IMailSender _emailSender;
    private readonly ILogger<RabbitMqListener> _logger;
    private readonly RabbitMqSettings _settings;
    
    public RabbitMqListener(
        IOptions<RabbitMqSettings> opts,
        IMailSender emailSender,
        ILogger<RabbitMqListener> logger)
    {
        _settings = opts.Value;
        _emailSender = emailSender;
        _logger = logger;
        _factory = new ConnectionFactory
        {
            AutomaticRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(5)
        };

        if (!string.IsNullOrEmpty(_settings.Uri))
        {
            _factory.Uri = new Uri(_settings.Uri);
        }
        else
        {
            _factory.HostName = _settings.HostName;
            _factory.Port = 5672;
            _factory.UserName = _settings.UserName;
            _factory.Password = _settings.Password;
            _factory.VirtualHost = "/";
        }
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // Mở connection bất đồng bộ
        _connection = await _factory.CreateConnectionAsync(cancellationToken)  // :contentReference[oaicite:0]{index=0}
            .ConfigureAwait(false);
        
        // Tạo channel bất đồng bộ
        _channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken)  // :contentReference[oaicite:1]{index=1}
            .ConfigureAwait(false);
        
        // Khai báo queue bất đồng bộ
        await _channel.QueueDeclareAsync(
                queue: _settings.QueueName,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: null,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_channel == null)
            throw new InvalidOperationException("RabbitMQ channel chưa được khởi tạo.");
        
        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += async (sender, ea) =>
        {
            try
            {
                var json  = Encoding.UTF8.GetString(ea.Body.ToArray());
                var email = JsonSerializer.Deserialize<EmailMessage>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (email != null)
                {
                    await _emailSender.SendEmailAsync(email);
                    await _channel!.BasicAckAsync(
                        deliveryTag: ea.DeliveryTag,
                        multiple: false,
                        cancellationToken: stoppingToken);
                    _logger.LogInformation("Email sent to {Recipient}", email.ToEmail);
                }
                else
                {
                    await _channel!.BasicNackAsync(
                        deliveryTag: ea.DeliveryTag,
                        multiple: false,
                        requeue: false,
                        cancellationToken: stoppingToken);
                    _logger.LogError("Deserialize EmailMessage failed: {Json}", json);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing RabbitMQ message");
            }
        };

        await _channel!.BasicConsumeAsync(
            queue:    _settings.QueueName,
            autoAck:  false,
            consumer: consumer, cancellationToken: stoppingToken);
    }
    
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_channel != null)
        {
            await _channel.CloseAsync(cancellationToken).ConfigureAwait(false);
            _channel.Dispose();
        }
        if (_connection != null)
        {
            await _connection.CloseAsync(cancellationToken).ConfigureAwait(false);
            _connection.Dispose();
        }
        await base.StopAsync(cancellationToken);
    }
}