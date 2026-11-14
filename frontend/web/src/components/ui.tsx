import { createContext, useContext, useEffect, useRef, useState, type ReactNode } from 'react';
import { AlertCircle, ArrowLeft, ArrowRight, Check, LoaderCircle, X } from 'lucide-react';
import { Link } from 'react-router-dom';

const ToastContext = createContext<(message: string) => void>(() => undefined);
export function ToastProvider({ children }: { children: ReactNode }) {
  const [message, setMessage] = useState('');
  useEffect(() => { if (!message) return; const timer = setTimeout(() => setMessage(''), 4500); return () => clearTimeout(timer); }, [message]);
  return <ToastContext.Provider value={setMessage}>{children}{message && <div className="toast" role="status"><Check size={18}/>{message}<button aria-label="Закрыть уведомление" onClick={() => setMessage('')}><X size={16}/></button></div>}</ToastContext.Provider>;
}
export const useToast = () => useContext(ToastContext);
export function Loading({ label = 'Загружаем события…' }: { label?: string }) { return <div className="state-block" role="status"><LoaderCircle className="spin"/><p>{label}</p></div>; }
export function ErrorState({ error, retry }: { error: unknown; retry?: () => void }) { return <div className="state-block error-state" role="alert"><AlertCircle/><h3>Не получилось загрузить данные</h3><p>{error instanceof Error ? error.message : 'Проверьте подключение и попробуйте ещё раз.'}</p>{retry && <button className="button secondary" onClick={retry}>Попробовать ещё раз</button>}</div>; }
export function Empty({ title, children, icon }: { title: string; children: ReactNode; icon?: ReactNode }) { return <div className="state-block empty-state">{icon}<h3>{title}</h3><div>{children}</div></div>; }
export function Field({ label, children, hint }: { label: string; children: ReactNode; hint?: string }) { return <label className="field"><span>{label}</span>{children}{hint && <small>{hint}</small>}</label>; }
export function Modal({ title, children, onClose }: { title: string; children: ReactNode; onClose: () => void }) {
  const dialog = useRef<HTMLDialogElement>(null); const close = useRef(onClose); close.current = onClose;
  useEffect(() => { const d = dialog.current!; d.showModal(); const cancel = (e: Event) => { e.preventDefault(); close.current(); }; d.addEventListener('cancel', cancel); return () => { d.removeEventListener('cancel', cancel); d.close(); }; }, []);
  return <dialog ref={dialog} className="modal" onClick={e => { if (e.target === dialog.current) onClose(); }} aria-labelledby="modal-title"><div className="modal-heading"><h2 id="modal-title">{title}</h2><button className="icon-button" aria-label="Закрыть окно" onClick={onClose}><X/></button></div>{children}</dialog>;
}
export function Pagination({ page, pages, onChange }: { page: number; pages: number; onChange: (page: number) => void }) {
  if (pages <= 1) return null;
  return <nav className="pagination" aria-label="Страницы результатов"><button className="button secondary" disabled={page <= 1} onClick={() => onChange(page - 1)}><ArrowLeft size={17}/><span>Назад</span></button><span>{page} / {pages}</span><button className="button secondary" disabled={page >= pages} onClick={() => onChange(page + 1)}><span>Дальше</span><ArrowRight size={17}/></button></nav>;
}
export function BackLink() { return <Link className="back-link" to="/"><ArrowLeft size={16}/> К афише</Link>; }
