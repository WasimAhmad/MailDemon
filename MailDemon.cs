﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using DnsClient;

using MailKit;
using MailKit.Net;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Configuration;
using MimeKit;

namespace MailDemon
{
    public class MailDemon : IDisposable
    {
        private class TcpListenerActive : TcpListener
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="T:System.Net.Sockets.TcpListener"/> class with the specified local endpoint.
            /// </summary>
            /// <param name="localEP">An <see cref="T:System.Net.IPEndPoint"/> that represents the local endpoint to which to bind the listener <see cref="T:System.Net.Sockets.Socket"/>. </param><exception cref="T:System.ArgumentNullException"><paramref name="localEP"/> is null. </exception>
            public TcpListenerActive(IPEndPoint localEP) : base(localEP)
            {
            }

            /// <summary>
            /// Initializes a new instance of the <see cref="T:System.Net.Sockets.TcpListener"/> class that listens for incoming connection attempts on the specified local IP address and port number.
            /// </summary>
            /// <param name="localaddr">An <see cref="T:System.Net.IPAddress"/> that represents the local IP address. </param><param name="port">The port on which to listen for incoming connection attempts. </param><exception cref="T:System.ArgumentNullException"><paramref name="localaddr"/> is null. </exception><exception cref="T:System.ArgumentOutOfRangeException"><paramref name="port"/> is not between <see cref="F:System.Net.IPEndPoint.MinPort"/> and <see cref="F:System.Net.IPEndPoint.MaxPort"/>. </exception>
            public TcpListenerActive(IPAddress localaddr, int port) : base(localaddr, port)
            {
            }

            public new bool Active
            {
                get { return base.Active; }
            }
        }

        private class SmtpMimeMessageStream : Stream
        {
            private Stream baseStream;
            private int state; // 0 = none, 1 = has \r, 2 = has \n, 3 = has ., 4 = has \r 5 = has \n done!

            public SmtpMimeMessageStream(Stream baseStream)
            {
                this.baseStream = baseStream;
            }

            public override bool CanRead => baseStream.CanRead;

            public override bool CanSeek => baseStream.CanSeek;

            public override bool CanWrite => baseStream.CanWrite;

            public override long Length => baseStream.Length;

            public override long Position { get => baseStream.Position; set => baseStream.Position = value; }

            public override void Flush()
            {
                baseStream.Flush();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (state == 5)
                {
                    return 0;
                }

                int read = baseStream.Read(buffer, offset, count);
                if (read > 0)
                {
                    int end = offset + read;
                    for (int i = offset; i < end; i++)
                    {
                        switch (state)
                        {
                            case 0:
                                if (buffer[i] == '\r')
                                {
                                    state = 1;
                                }
                                else
                                {
                                    state = 0;
                                }
                                break;

                            case 1:
                                if (buffer[i] == '\n')
                                {
                                    state = 2;
                                }
                                else
                                {
                                    state = 0;
                                }
                                break;

                            case 2:
                                if (buffer[i] == '.')
                                {
                                    state = 3;
                                }
                                else if (buffer[i] == '\r')
                                {
                                    state = 1;
                                }
                                else
                                {
                                    state = 0;
                                }
                                break;

                            case 3:
                                if (buffer[i] == '\r')
                                {
                                    state = 4;
                                }
                                else
                                {
                                    state = 0;
                                }
                                break;

                            case 4:
                                if (buffer[i] == '\n')
                                {
                                    state = 5;
                                    return read;
                                }
                                else
                                {
                                    state = 0;
                                }
                                break;
                        }
                    }
                }
                return read;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                return baseStream.Seek(offset, origin);
            }

            public override void SetLength(long value)
            {
                baseStream.SetLength(value);
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                baseStream.Write(buffer, offset, count);
            }
        }

        private class MailDemonUser
        {
            public MailDemonUser(string name, string password)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new ArgumentException("Name must not be null or empty", nameof(name));
                }
                if (string.IsNullOrWhiteSpace(password))
                {
                    throw new ArgumentException("Password must not be null or empty", nameof(password));
                }
                Name = name;
                Password = password;
                Plain = string.Format("\0{0}\0{1}", name, password);
            }

            public string Name { get; private set; }
            public string Password { get; private set; }
            public string Plain { get; private set; }
        }

        private TcpListenerActive server;
        private IPAddress ip;
        private int port;
        private readonly List<MailDemonUser> users = new List<MailDemonUser>();
        private X509Certificate2 sslCertificate;
        private Dictionary<string, Regex> ignoreCertificateErrorsRegex = new Dictionary<string, Regex>(StringComparer.OrdinalIgnoreCase); // domain,regex

        public string Domain { get; private set; }

        private void ParseConfiguration(string[] args, IConfiguration configuration)
        {
            IConfigurationSection rootSection = configuration.GetSection("mailDemon");
            Domain = (rootSection["domain"] ?? Domain);
            ip = (string.IsNullOrWhiteSpace(rootSection["ip"]) ? IPAddress.Any : IPAddress.Parse(rootSection["ip"]));
            if (!int.TryParse(rootSection["port"], out port))
            {
                port = 25;
            }
            IConfigurationSection userSection = rootSection.GetSection("users");
            foreach (var child in userSection.GetChildren())
            {
                users.Add(new MailDemonUser(child["name"], child["password"]));
            }
            string sslCertificateFile = rootSection["sslCertificate"];
            if (!string.IsNullOrWhiteSpace(sslCertificateFile))
            {
                sslCertificate = new X509Certificate2(sslCertificateFile, rootSection["sslCertificatePassword"]);
            }
            IConfigurationSection ignoreRegexSection = rootSection.GetSection("ignoreCertificateErrorsRegex");
            if (ignoreRegexSection != null)
            {
                foreach (var child in ignoreRegexSection.GetChildren())
                {
                    Regex re = new Regex(child["regex"].ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
                    foreach (var domain in child.GetSection("domains").GetChildren())
                    {
                        ignoreCertificateErrorsRegex[domain.Value] = re;
                    }
                }
            }
        }

        public async Task RunAsync(string[] args, IConfiguration configuration)
        {
            ParseConfiguration(args, configuration);
            server = new TcpListenerActive(IPAddress.Any, 25);
            server.Start();
            Console.CancelKeyPress += Console_CancelKeyPress;
            Console.WriteLine("Mail Demon Running, Press Ctrl-C to quit");

            while (server.Active)
            {
                using (TcpClient client = await server.AcceptTcpClientAsync())
                {
                    try
                    {
                        string ipAddress = client.Client.RemoteEndPoint.ToString();
                        MailDemonUser foundUser = null;
                        using (NetworkStream stream = client.GetStream())
                        {
                            // create comm streams
                            StreamReader reader = new StreamReader(stream, Encoding.UTF8);
                            StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };
                            SslStream sslStream = null;
                            Console.WriteLine("Connection accepted from {0}.", ipAddress);

                            // send greeting
                            await writer.WriteLineAsync($"220 {Domain} ESMTP & MailDemon &");

                            bool authenticated = false;
                            bool ehlo = false;

                            while (true)
                            {
                                // read initial client string
                                string line = await reader.ReadLineAsync() ?? string.Empty;
                                if (line.StartsWith("EHLO"))
                                {
                                    await writer.WriteLineAsync($"250-SIZE 104857600");
                                    await writer.WriteLineAsync($"250-8BITMIME");
                                    await writer.WriteLineAsync($"250-AUTH PLAIN");
                                    await writer.WriteLineAsync($"250-PIPELINING");
                                    if (sslCertificate != null && sslStream == null)
                                    {
                                        await writer.WriteLineAsync($"250-STARTTLS");
                                    }
                                    await writer.WriteLineAsync($"250-SMTPUTF8");
                                    await writer.WriteLineAsync($"250-BINARYMIME");
                                    await writer.WriteLineAsync($"250 CHUNKING");
                                    ehlo = true;
                                }
                                else if (line.StartsWith("HELO"))
                                {
                                    await writer.WriteLineAsync($"220 {Domain} Hello {line.Substring(5)}");
                                }
                                else if (line.StartsWith("AUTH PLAIN"))
                                {
                                    if (line == "AUTH PLAIN")
                                    {
                                        await writer.WriteLineAsync($"334");
                                        line = await reader.ReadLineAsync() ?? string.Empty;
                                    }
                                    else
                                    {
                                        line = line.Substring(11);
                                    }
                                    foundUser = null;
                                    string sentAuth = Encoding.UTF8.GetString(Convert.FromBase64String(line));
                                    foreach (MailDemonUser user in users)
                                    {
                                        if (user.Plain == sentAuth)
                                        {
                                            foundUser = user;
                                            break;
                                        }
                                    }
                                    if (foundUser != null)
                                    {
                                        Console.WriteLine("User {0} authenticated", foundUser.Name);
                                        await writer.WriteLineAsync($"235 2.7.0 Accepted");
                                        authenticated = true;
                                    }
                                    else
                                    {
                                        // fail
                                        Console.WriteLine("Authentication failed: {0}", sentAuth);
                                        await writer.WriteLineAsync($"535 authentication failed");
                                        throw new IOException("Authentication failed");
                                    }
                                }
                                else if (ehlo && sslStream == null && line.StartsWith("STARTTLS"))
                                {
                                    if (sslCertificate == null)
                                    {
                                        await writer.WriteLineAsync("501 Syntax error (no parameters allowed)");
                                    }
                                    else
                                    {
                                        // upgrade to ssl
                                        await writer.WriteLineAsync($"220 Ready to start TLS");

                                        sslStream = new SslStream(stream, false, null, null, EncryptionPolicy.RequireEncryption);

                                        // this can hang if the client does not authenticate ssl properly, so we kill it after 5 seconds
                                        if (!sslStream.AuthenticateAsServerAsync(sslCertificate, false, System.Security.Authentication.SslProtocols.Tls12, true).Wait(5000))
                                        {
                                            // forces the authenticate as server to fail and throw exception
                                            sslStream.Dispose();
                                        }

                                        // create comm streams on top of ssl stream
                                        reader = new StreamReader(sslStream, Encoding.UTF8);
                                        writer = new StreamWriter(sslStream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };
                                        ehlo = false;
                                    }
                                }
                                else if (authenticated)
                                {
                                    if (line.StartsWith("MAIL FROM:<"))
                                    {
                                        await writer.WriteLineAsync($"250 2.1.0 OK");
                                        line = await reader.ReadLineAsync();
                                        List<string> addresses = new List<string>();
                                        while (line.StartsWith("RCPT TO:<"))
                                        {
                                            addresses.Add(line.Substring(9).Trim('>'));
                                            await writer.WriteLineAsync($"250 2.1.0 OK");
                                            line = await reader.ReadLineAsync();
                                        }
                                        if (line == "DATA")
                                        {
                                            await writer.WriteLineAsync($"354");
                                            SmtpMimeMessageStream mimeStream = new SmtpMimeMessageStream(reader.BaseStream);
                                            MimeMessage mimeMessage = await MimeMessage.LoadAsync(mimeStream, true);
                                            await writer.WriteLineAsync($"250 2.0.0 OK");
                                            await SendMessage(mimeMessage, foundUser.Name + "@" + Domain, addresses);
                                        }
                                        else
                                        {
                                            throw new InvalidOperationException("Invalid smtp line");
                                        }
                                    }
                                    else
                                    {
                                        break; // disconnect
                                    }
                                }
                                else
                                {
                                    throw new InvalidOperationException("Invalid line: " + line + ", not authenticated");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex);
                    }
                }
            }
        }

        public void Dispose()
        {
            server?.Server?.Close();
            server = null;
        }

        private void Console_CancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            Dispose();
        }

        private async Task SendMessage(MimeMessage msg, string from, IEnumerable<string> addresses)
        {
            Console.WriteLine("Sending from {0}", from);
            string toDomain = null;
            using (SmtpClient client = new SmtpClient()
            {
                ServerCertificateValidationCallback = (object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors) =>
                {
                    return (sslPolicyErrors == SslPolicyErrors.None ||
                        (ignoreCertificateErrorsRegex.TryGetValue(toDomain, out Regex re) && re.IsMatch(certificate.Subject)));
                }
            })
            {
                foreach (string address in addresses)
                {
                    IPHostEntry ip = null;
                    int pos = address.IndexOf('@');
                    bool sent = false;
                    if (pos >= 0)
                    {
                        toDomain = address.Substring(++pos);
                        LookupClient lookup = new LookupClient();
                        IDnsQueryResponse result = await lookup.QueryAsync(toDomain, QueryType.MX);
                        foreach (DnsClient.Protocol.MxRecord record in result.AllRecords)
                        {
                            // attempt to send, if fail, try next address
                            try
                            {
                                ip = await Dns.GetHostEntryAsync(record.Exchange);
                                foreach (IPAddress ipAddress in ip.AddressList)
                                {
                                    string host = ip.HostName;
                                    try
                                    {
                                        await client.ConnectAsync(host, options: MailKit.Security.SecureSocketOptions.StartTlsWhenAvailable);
                                        await client.SendAsync(msg);
                                        sent = true;
                                        break;
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine("Error sending message: {0}", ex);
                                    }
                                    finally
                                    {
                                        await client.DisconnectAsync(true);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine("Failed to send email via {0}, trying next entry. Error: {1}.", ip, ex);
                            }

                            if (sent)
                            {
                                break;
                            }
                        }
                    }
                }
            }
        }
    }
}
