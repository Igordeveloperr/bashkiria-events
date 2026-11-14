import { Component, Suspense, lazy, type ReactNode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter, Route, Routes, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { Layout } from './components/Layout';
import { Catalog } from './pages/Catalog';
import { EventDetail } from './pages/EventDetail';
import { Favorites, FeedbackPage, Notifications, Subscriptions } from './pages/Personal';
import { About, Embed, NotFound, WidgetBrand } from './pages/Misc';
import { Loading, ToastProvider } from './components/ui';
import { FavoritesProvider } from './lib/store';
import { useEffect } from 'react';
import './styles.css';

const Admin = lazy(() => import('./pages/Admin').then(module => ({ default: module.Admin })));
const client = new QueryClient({ defaultOptions: { queries: { retry: 1, staleTime: 20000, refetchOnWindowFocus: true }, mutations: { retry: false } } });
class ErrorBoundary extends Component<{ children: ReactNode }, { failed: boolean }> {
  state = { failed: false };
  static getDerivedStateFromError() { return { failed: true }; }
  render() { return this.state.failed ? <div className="state-block"><h1>Что-то пошло не так</h1><p>Обновите страницу. Ваши подписки сохранятся.</p><a className="button primary" href="/">Вернуться к афише</a></div> : this.props.children; }
}
function ScrollTop() { const { pathname } = useLocation(); useEffect(() => { window.scrollTo(0, 0); document.title = pathname === '/' ? 'Башкирия — события рядом' : pathname === '/admin' ? 'Модератор · Башкирия' : 'Культурная афиша · Башкирия'; }, [pathname]); return null; }
createRoot(document.getElementById('root')!).render(<QueryClientProvider client={client}><BrowserRouter><ErrorBoundary><ToastProvider><FavoritesProvider><ScrollTop/><Suspense fallback={<Loading/>}><Routes>
  <Route path="/widget" element={<div className="widget-root"><WidgetBrand/><Catalog widget/></div>}/>
  <Route element={<Layout/>}><Route index element={<Catalog/>}/><Route path="events/:id" element={<EventDetail/>}/><Route path="favorites" element={<Favorites/>}/><Route path="subscriptions" element={<Subscriptions/>}/><Route path="notifications" element={<Notifications/>}/><Route path="feedback" element={<FeedbackPage/>}/><Route path="about" element={<About/>}/><Route path="embed" element={<Embed/>}/><Route path="admin" element={<Admin/>}/><Route path="*" element={<NotFound/>}/></Route>
</Routes></Suspense></FavoritesProvider></ToastProvider></ErrorBoundary></BrowserRouter></QueryClientProvider>);
