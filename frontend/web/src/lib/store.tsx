import { createContext, useContext, useState, type ReactNode } from 'react';
import { useQuery } from '@tanstack/react-query';
import { api, session } from './api';
import type { Category, City } from './types';

export function useSession() { return useQuery({ queryKey: ['session'], queryFn: session, staleTime: 300000, retry: 1 }); }
export function useCities() { return useQuery({ queryKey: ['cities'], queryFn: () => api<City[]>('/api/cities'), staleTime: 60000 }); }
export function useCategories() { return useQuery({ queryKey: ['categories'], queryFn: () => api<Category[]>('/api/categories'), staleTime: 60000 }); }
const FavoritesContext = createContext<{ ids: string[]; toggle: (id: string) => void }>({ ids: [], toggle: () => undefined });
export function FavoritesProvider({ children }: { children: ReactNode }) {
  const [ids, setIds] = useState<string[]>(() => {
    try { const value: unknown = JSON.parse(localStorage.getItem('bashkiria:favorites') ?? '[]'); return Array.isArray(value) ? value.filter(x => typeof x === 'string').slice(0, 50) : []; } catch { return []; }
  });
  function toggle(id: string) { setIds(current => { const next = current.includes(id) ? current.filter(x => x !== id) : [...current, id].slice(-50); try { localStorage.setItem('bashkiria:favorites', JSON.stringify(next)); } catch { /* Storage is optional in private/embedded browsers. */ } return next; }); }
  return <FavoritesContext.Provider value={{ ids, toggle }}>{children}</FavoritesContext.Provider>;
}
export const useFavorites = () => useContext(FavoritesContext);
