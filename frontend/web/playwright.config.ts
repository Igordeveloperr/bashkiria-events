import { defineConfig } from '@playwright/test';
export default defineConfig({ testDir: './e2e', workers: 1, timeout: 45000, use: { baseURL: process.env.WEB_TEST_URL ?? 'http://localhost:8080', channel: 'msedge', viewport: { width: 1440, height: 1000 }, screenshot: 'only-on-failure' } });
