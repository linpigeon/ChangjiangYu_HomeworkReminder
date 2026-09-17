// Minimal CONNECT tunnel proxy.
//
// Why this exists: in this sandbox, .NET's TLS stack cannot build a credential
// handle ("安全包中没有可用的凭证" / SEC_E_NO_CREDENTIALS), so `dotnet restore`
// fails against api.nuget.org. Node's OpenSSL stack is unaffected. This proxy
// tunnels raw TCP so .NET negotiates its own TLS end-to-end — no interception,
// no certificate handling. It is a build-environment workaround only.
import net from 'node:net';
import http from 'node:http';

const PORT = Number(process.env.TUNNEL_PORT ?? 8899);
const HOST = '127.0.0.1';

const server = http.createServer((req, res) => {
  // Plain HTTP proxying (rarely needed, but keeps behaviour sane).
  const target = new URL(req.url);
  const upstream = http.request(
    {
      host: target.hostname,
      port: target.port || 80,
      path: target.pathname + target.search,
      method: req.method,
      headers: { ...req.headers, host: target.host },
    },
    (up) => {
      res.writeHead(up.statusCode ?? 502, up.headers);
      up.pipe(res);
    },
  );
  upstream.on('error', () => res.destroy());
  req.pipe(upstream);
});

server.on('connect', (req, clientSocket, head) => {
  const [host, portText] = req.url.split(':');
  const port = Number(portText) || 443;
  const upstream = net.connect(port, host, () => {
    clientSocket.write('HTTP/1.1 200 Connection Established\r\n\r\n');
    if (head?.length) upstream.write(head);
    upstream.pipe(clientSocket);
    clientSocket.pipe(upstream);
  });
  const fail = () => {
    clientSocket.destroy();
    upstream.destroy();
  };
  upstream.on('error', fail);
  clientSocket.on('error', fail);
});

server.listen(PORT, HOST, () => {
  console.log(`tunnel proxy listening on http://${HOST}:${PORT}`);
});
