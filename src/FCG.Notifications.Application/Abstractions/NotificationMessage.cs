namespace FCG.Notifications.Application.Abstractions;

public sealed record NotificationMessage 
(
    string Recipient, 
    string Subject, 
    string Body
);