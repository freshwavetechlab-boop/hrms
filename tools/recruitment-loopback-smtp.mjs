import net from 'node:net'

// Same minimal protocol as LoopbackInterviewSmtp.cs. Synthetic recipients only;
// no forwarding and no MIME bodies/credentials written to disk.
export async function startRecruitmentMailSink() {
  const deliveries = [], sockets = new Set()
  const server = net.createServer(socket => {
    sockets.add(socket); socket.on('close', () => sockets.delete(socket)); socket.on('error', () => {})
    socket.setEncoding('utf8'); socket.setTimeout(15000, () => socket.destroy())
    socket.write('220 loopback.invalid ESMTP test sink\r\n')
    let buffer = '', data = false, bytes = 0, recipients = []
    socket.on('data', chunk => {
      buffer += chunk
      let index
      while ((index = buffer.indexOf('\r\n')) >= 0) {
        const line = buffer.slice(0, index); buffer = buffer.slice(index + 2)
        if (data) {
          if (line === '.') { deliveries.push(...recipients.map(recipient => ({ recipient, bytes }))); data = false; socket.write('250 Accepted\r\n') }
          else { bytes += Buffer.byteLength(line); if (bytes > 1000000) socket.destroy() }
        } else if (/^(EHLO|HELO) /i.test(line)) socket.write('250 loopback.invalid\r\n')
        else if (/^MAIL FROM:/i.test(line)) { recipients = []; socket.write('250 OK\r\n') }
        else if (/^RCPT TO:/i.test(line)) {
          const address = line.match(/<([^>]+)>/)?.[1] || ''
          if (!address.toLowerCase().endsWith('@example.invalid')) socket.write('550 Only synthetic recipients allowed\r\n')
          else { recipients.push(address); socket.write('250 OK\r\n') }
        } else if (line === 'DATA') { data = true; bytes = 0; socket.write('354 End with a single dot\r\n') }
        else if (line === 'QUIT') socket.end('221 Bye\r\n')
        else if (line === 'RSET') { recipients = []; socket.write('250 OK\r\n') }
        else socket.write('502 Unsupported\r\n')
      }
    })
  })
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
  return { port: server.address().port, deliveries, close: async () => { for (const socket of sockets) socket.destroy(); await new Promise(resolve => server.close(resolve)) } }
}
