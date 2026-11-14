interface TelegramApp {
  initData: string; colorScheme?: string;
  ready(): void; expand(): void;
  BackButton: { show(): void; hide(): void; onClick(fn: () => void): void; offClick(fn: () => void): void };
  onEvent(event: string, fn: () => void): void; offEvent(event: string, fn: () => void): void;
}
declare global { interface Window { Telegram?: { WebApp: TelegramApp } } }
export async function telegram(): Promise<TelegramApp | null> {
  if (!location.hash.includes('tgWebApp') && !window.Telegram?.WebApp?.initData) return null;
  if (!window.Telegram) await new Promise<void>((resolve, reject) => {
    const script = document.createElement('script'); script.src = 'https://telegram.org/js/telegram-web-app.js'; script.onload = () => resolve(); script.onerror = reject;
    document.head.append(script); setTimeout(() => reject(new Error('Telegram SDK timeout')), 10000);
  }).catch(() => undefined);
  const app = window.Telegram?.WebApp;
  if (!app?.initData) return null;
  app.ready(); app.expand(); document.documentElement.classList.add('telegram-app'); return app;
}
