import { int64 } from './src/utils/int64.js';
// Same socket/ROP transport as the established Haze sequence. No kernel rerun.
export async function sendPayload(item, p, chain) {
  if (p.assert_worker_usable) p.assert_worker_usable();
  const response = await fetch(item.file);
  if (!response.ok) throw new Error('Download failed: HTTP ' + response.status);
  const bytes = new Uint8Array(await response.arrayBuffer());
  if (bytes.length !== item.bytes || bytes[0] !== 0x7f || bytes[1] !== 0x45 || bytes[2] !== 0x4c || bytes[3] !== 0x46)
    throw new Error('Payload file is incomplete or not an ELF');
  if (!crypto.subtle) throw new Error('Verified payload loading requires HTTPS');
  const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', bytes));
  const hash = Array.from(digest, byte => byte.toString(16).padStart(2,'0')).join('');
  if (hash !== item.sha256) throw new Error('Payload checksum does not match the official release');
  const size = (bytes.length + 0x3fff) & ~0x3fff;
  const base = await chain.syscall(SYS_MMAP, 0, size, 3, 0x1002, -1, 0);
  if ((base.low >>> 0) === 0xffffffff || base.low < 0x10000) throw new Error('Could not allocate payload memory');
  let fd = -1;
  try {
    const view = new DataView(bytes.buffer);
    const dwords = bytes.length & ~3;
    for (let i=0;i<dwords;i+=4) p.write4(base.add32(i),view.getUint32(i,true));
    for (let i=dwords;i<bytes.length;i++) p.write1(base.add32(i),bytes[i]);
    const address = p.malloc(16);
    p.write8(address,new int64(0,0)); p.write8(address.add32(8),new int64(0,0));
    p.write4(address,0x3d230210); p.write4(address.add32(4),0x0100007f);
    for (let attempt=0;attempt<12;attempt++) {
      const socket = await chain.syscall(SYS_SOCKET,2,1,0); fd = socket.low|0;
      if (fd>=0) {
        const connected = await chain.syscall(SYS_CONNECT,fd,address,16);
        if ((connected.low>>>0)===0) break;
        await chain.syscall(SYS_CLOSE,fd); fd=-1;
      }
      await new Promise(resolve=>setTimeout(resolve,250));
    }
    if (fd<0) throw new Error('The ELF loader is not accepting connections on port 9021');
    for (let offset=0;offset<bytes.length;) {
      const count = Math.min(0x10000,bytes.length-offset);
      const written = (await chain.syscall(SYS_WRITE,fd,base.add32(offset),count)).low|0;
      if (written<=0 || written>count) throw new Error('Payload transfer stopped before completion');
      offset+=written;
    }
    return bytes.length;
  } finally {
    // A failed worker must not receive any further native calls.
    if (!p.worker_is_usable || p.worker_is_usable()) {
      try { if (fd>=0) await chain.syscall(SYS_CLOSE,fd); } finally {
        if (!p.worker_is_usable || p.worker_is_usable()) await chain.syscall(SYS_MUNMAP,base,size);
      }
    }
  }
}