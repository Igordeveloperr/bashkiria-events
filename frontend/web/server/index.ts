import { createServer } from './app.ts';
import { config } from './config.ts';

const app = await createServer();
await app.listen({ host: config.host, port: config.port });
console.log(`Web server listening on ${config.host}:${config.port}`);
for (const signal of ['SIGTERM', 'SIGINT'] as const) process.once(signal, async () => { await app.close(); process.exit(0); });
