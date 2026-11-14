import { useEffect } from 'react';
import { Bell, Bookmark, CalendarDays, ChevronDown, Flower2, MapPin, MessageSquare, ShieldCheck } from 'lucide-react';
import { Link, NavLink, Outlet, useLocation, useNavigate, useSearchParams } from 'react-router-dom';
import { useQueryClient } from '@tanstack/react-query';
import { useCities, useSession } from '../lib/store';
import { api, setCsrf } from '../lib/api';
import { telegram } from '../lib/telegram';
import type { Session } from '../lib/types';

const links = [{ to: '/', title: 'Афиша', icon: CalendarDays }, { to: '/favorites', title: 'Избранное', icon: Bookmark }, { to: '/subscriptions', title: 'Подписки', icon: Bell }, { to: '/assistant', title: 'Помощник', icon: MessageSquare }];
export function Layout() {
  const { data: cities = [] } = useCities(); const current = useSession(); const query = useQueryClient();
  const [params, setParams] = useSearchParams(); const location = useLocation(); const navigate = useNavigate();
  useEffect(() => {
    let dispose: (() => void) | undefined; let cancelled = false;
    void telegram().then(async app => {
      if (!app || cancelled) return;
      const back = () => navigate(-1); app.BackButton.onClick(back);
      if (window.location.pathname === '/') app.BackButton.hide(); else app.BackButton.show();
      const theme = () => { document.documentElement.dataset.theme = app.colorScheme === 'dark' ? 'dark' : 'light'; };
      theme(); app.onEvent('themeChanged', theme);
      dispose = () => { app.BackButton.offClick(back); app.offEvent('themeChanged', theme); };
      if (current.data?.telegramAvailable && !current.data.telegram) {
        const value = await api<Session>('/api/session/telegram', { method: 'POST', body: JSON.stringify({ initData: app.initData }) }).catch(() => null);
        if (value) { setCsrf(value.csrfToken); query.setQueryData(['session'], value); void query.invalidateQueries({ queryKey: ['me'] }); }
      }
    });
    return () => { cancelled = true; dispose?.(); };
  }, [current.data?.telegramAvailable, current.data?.telegram, navigate, query, location.pathname]);
  return <><a className="skip-link" href="#main">Перейти к содержимому</a>
    <header className="site-header"><div className="header-inner"><Link className="brand" to="/" aria-label="Башкирия. На главную"><span className="brand-mark"><Flower2 size={29}/></span><span>башкирия<span className="brand-dot">.</span><small>события рядом</small></span></Link>
      <nav className="desktop-nav" aria-label="Основная навигация">{links.map(link => <NavLink end={link.to === '/'} key={link.to} to={link.to}>{link.title}</NavLink>)}</nav>
      <div className="header-actions"><label className="header-city"><MapPin size={17}/><span className="sr-only">Город</span><select aria-label="Выбрать город" value={location.pathname === '/' ? params.get('city') ?? '' : ''} onChange={e => {
        if (location.pathname !== '/') { navigate(e.target.value ? `/?city=${encodeURIComponent(e.target.value)}` : '/'); return; }
        const next = new URLSearchParams(params); if (e.target.value) next.set('city', e.target.value); else next.delete('city'); next.delete('page'); setParams(next);
      }}><option value="">Весь Башкортостан</option>{cities.map(city => <option key={city.id} value={city.name}>{city.name}</option>)}</select><ChevronDown size={14}/></label>
      <NavLink className="icon-button notifications-link" to="/notifications" aria-label="Уведомления"><Bell size={20}/></NavLink></div>
    </div></header>
    <main id="main" tabIndex={-1}><Outlet/></main>
    <footer className="site-footer"><div className="footer-top"><Link className="brand small" to="/"><span className="brand-mark"><Flower2 size={24}/></span><span>башкирия<span className="brand-dot">.</span><small>события рядом</small></span></Link><div className="footer-links"><Link to="/about">О проекте</Link><Link to="/feedback">Обратная связь</Link><Link to="/embed">Виджет</Link><Link to="/admin"><ShieldCheck size={15}/>Модератору</Link></div></div><div className="footer-bottom"><p>Культура ближе, чем кажется.</p><span>Независимая афиша. Не является официальным сервисом министерства.</span></div></footer>
    <nav className="bottom-nav" aria-label="Мобильная навигация">{links.map(link => <NavLink end={link.to === '/'} to={link.to} key={link.to}><link.icon size={21}/><span>{link.title}</span></NavLink>)}</nav>
  </>;
}
