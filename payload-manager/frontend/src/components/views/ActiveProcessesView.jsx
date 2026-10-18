import { createRequestScope } from '../../utils/requestScope'
import { apiFetch, mutationFetch } from '../../utils/mutationRequest'
import React, { useState, useEffect, useMemo, useCallback, useRef } from 'react'
import { Cpu, RefreshCw, XCircle, Search, AlertTriangle, Activity, Loader2, Info, Trash2 } from 'lucide-react'
import { useTranslation, Trans } from 'react-i18next'
import { cn } from '../../utils/helpers'

const ActiveProcessesView = ({ addToast, showConfirm }) => {
  const { t } = useTranslation()
  const [processes, setProcesses] = useState([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(false)
  const [showAll, setShowAll] = useState(false)
  const [search, setSearch] = useState('')

  const requestRef = useRef(null)
  const killRef = useRef(null)
  const aliveRef = useRef(false)
  const refreshTimerRef = useRef(null)
  const fetchProcesses = useCallback(async () => {
    requestRef.current?.abort()
    const controller = createRequestScope()
    requestRef.current = controller
    try {
      const res = await apiFetch('/processes_list', { signal: controller.signal, cache: 'no-store' })
      if (!res.ok) throw new Error()
      const data = await res.json()
      if (controller.aborted) return
      if (!Array.isArray(data?.processes)) throw new Error()
      const entries = data.processes.filter(p => Number.isInteger(p.pid) && p.pid >= 0 &&
        typeof p.name === 'string' && Number.isFinite(p.memory) && p.memory >= 0)
      setProcesses(entries)
      setError(false)
    } catch {
      if (!controller.aborted) setError(true)
    } finally {
      if (!controller.aborted) setLoading(false)
    }
  }, [])
  const refresh = () => { setLoading(true); setError(false); void fetchProcesses() }
  useEffect(() => {
    aliveRef.current = true
    // StrictMode's setup/cleanup probe cancels this startup task before it can
    // issue a duplicate request; initial loading is already visible.
    const initialId = setTimeout(fetchProcesses, 0)
    const intervalId = setInterval(fetchProcesses, 15000)
    return () => {
      aliveRef.current = false
      clearTimeout(initialId)
      clearInterval(intervalId)
      clearTimeout(refreshTimerRef.current)
      requestRef.current?.abort()
      killRef.current?.abort()
    }
  }, [fetchProcesses])

  const filteredProcesses = useMemo(() => {
    let result = processes
    if (!showAll) {
      result = result.filter(p => p.is_daemon)
    }
    if (search.trim() !== '') {
      const q = search.toLowerCase()
      result = result.filter(p => p.name.toLowerCase().includes(q))
    }
    return result
  }, [processes, showAll, search])

  const handleKill = (proc) => {
    const isCritical = proc.name === 'pldmgr.elf' || proc.name === 'elfldr.elf';
    if (isCritical) {
      addToast(t("active_processes.cannot_kill", "Cannot kill {{name}}", { name: proc.name }), "error")
      return
    }

    showConfirm(
      t("active_processes.kill_modal_title", "Kill Process"),
      t("active_processes.kill_modal_message", "Are you sure you want to kill {{name}} (PID: {{pid}})?", { name: proc.name, pid: proc.pid }),
      async () => {
        if (!aliveRef.current) return
        const controller = createRequestScope()
        killRef.current = controller
        try {
          const res = await mutationFetch(`/process_kill?pid=${proc.pid}`, { signal: controller.signal })
          const data = await res.json()
          if (controller.aborted) return
          if (res.ok) {
            setProcesses(prev => prev.filter(p => p.pid !== proc.pid));
            addToast(t("active_processes.kill_success", "Successfully killed {{name}}", { name: proc.name }))
            clearTimeout(refreshTimerRef.current);
            refreshTimerRef.current = setTimeout(fetchProcesses, 1000);
          } else {
            addToast(data.error || t("active_processes.kill_failed", "Failed to kill {{name}}", { name: proc.name }), "error")
          }
        } catch {
          if (controller.aborted) return
          addToast(t("active_processes.kill_error", "Error killing {{name}}", { name: proc.name }), "error")
        }
      }
    )
  }

  return (
    <div className="space-y-12">
      <div className="flex flex-col md:flex-row md:items-center justify-between gap-8">
        <h2 className="text-4xl font-extrabold text-white tracking-tight">
          {t("active_processes.title_active", "Active")} <span className="text-ps-blue">{t("active_processes.title_processes", "Processes")}</span>
        </h2>
        <div className="flex items-center space-x-4">
          <label className="flex items-center space-x-3 cursor-pointer group">
            <div className="relative inline-flex h-6 w-11 items-center rounded-full transition-colors focus:outline-none focus:ring-2 focus:ring-ps-blue focus:ring-offset-2 focus:ring-offset-black bg-white/10 group-hover:bg-white/20">
              <input
                type="checkbox"
                className="sr-only"
                checked={showAll}
                onChange={(e) => setShowAll(e.target.checked)}
              />
              <span
                className={cn(
                  "inline-block h-4 w-4 transform rounded-full bg-white transition-transform",
                  showAll ? "translate-x-6 bg-ps-blue" : "translate-x-1"
                )}
              />
            </div>
            <span className="text-sm font-bold text-zinc-300 select-none cursor-pointer">
              {t("active_processes.show_all", "Show All System Processes")}
            </span>
          </label>
        </div>
      </div>

      <section className="space-y-6">
        <div className="flex items-center bg-black/40 border border-white/10 rounded-2xl px-4 py-3 focus-within:border-ps-blue/50 transition-colors">
          <Search className="w-5 h-5 text-zinc-500 mr-3" />
          <input
            type="text"
            placeholder={t("active_processes.search_placeholder", "Search processes by name...")}
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            className="bg-transparent border-none outline-none text-white w-full font-medium placeholder:text-zinc-600"
          />
        </div>

        {loading && processes.length === 0 ? (
          <div className="py-20 flex flex-col items-center justify-center space-y-6">
            <Loader2 className="w-12 h-12 text-ps-blue animate-spin" />
            <p className="label-caps animate-pulse text-zinc-500">{t("active_processes.fetching", "Fetching process list...")}</p>
          </div>
        ) : error ? (
          <div className="py-12 bg-red-500/10 border border-red-500/20 rounded-2xl flex flex-col items-center justify-center space-y-4">
            <AlertTriangle className="w-10 h-10 text-red-500" />
            <p className="text-red-400 font-bold">{t("active_processes.error_loading", "Failed to load processes")}</p>
            <button onClick={refresh} className="px-6 py-2 bg-red-500 hover:bg-red-400 text-white rounded-xl font-bold transition-colors">
              {t("active_processes.retry", "Retry")}
            </button>
          </div>
        ) : filteredProcesses.length === 0 ? (
          <div className="py-20 flex flex-col items-center justify-center space-y-6 bg-white/[0.02] border-2 border-dashed border-white/5 rounded-2xl">
            <Cpu className="w-12 h-12 text-zinc-600" />
            <p className="text-zinc-500 font-bold text-lg">{t("active_processes.no_processes", "No processes found")}</p>
          </div>
        ) : (
          <div className="grid grid-cols-1 gap-4">
            {filteredProcesses.map((p) => {
              const critical = p.name === 'pldmgr.elf' || p.name === 'elfldr.elf';
              return (
                <div key={p.pid} className={cn(
                  "glass-card p-4 md:p-6 rounded-2xl flex flex-row items-center justify-between gap-4 border-white/10 hover:border-ps-blue/20 transition-all bg-white/[0.01]"
                )}>
                  <div className="flex items-center space-x-4 min-w-0 flex-1">
                    <div className={cn(
                      "p-3 rounded-xl flex-shrink-0",
                      p.is_daemon ? "bg-ps-blue/10 text-ps-blue" : "bg-white/5 text-zinc-400"
                    )}>
                      <Cpu className="w-6 h-6" />
                    </div>
                    <div className="min-w-0 flex-1 space-y-1">
                      <h3 className="text-xl font-bold text-white truncate">{p.name}</h3>
                      <div className="flex items-center space-x-4 text-xs font-mono text-zinc-500 uppercase tracking-wider">
                        <span>PID: <span className="text-zinc-300">{p.pid}</span></span>
                        <span>MEM: <span className="text-zinc-300">{p.memory.toFixed(1)} MiB</span></span>
                      </div>
                    </div>
                  </div>
                  <button
                    onClick={() => handleKill(p)}
                    disabled={critical}
                    className="px-4 py-2 bg-red-500/10 hover:bg-red-500 text-red-500 hover:text-white rounded-xl font-bold text-sm transition-all disabled:opacity-30 disabled:cursor-not-allowed group-hover:opacity-100 opacity-50 flex items-center space-x-2"
                    title={critical ? t("active_processes.cannot_kill_tooltip", "Cannot kill critical process") : t("active_processes.kill_button", "Kill")}
                  >
                    <Trash2 className="w-4 h-4" />
                    <span className="hidden sm:inline">{t("active_processes.kill_button", "Kill")}</span>
                  </button>
                </div>
              )
            })}
          </div>
        )}

        <div className="flex items-start space-x-3 text-zinc-500 text-sm p-6 glass-card rounded-2xl border-white/5 bg-white/[0.01]">
          <Info className="w-5 h-5 shrink-0 text-ps-blue" />
          <div>
            <p className="font-bold text-white mb-1">{t("active_processes.note_title", "Note:")}</p>
            <p className="leading-relaxed">
              <Trans
                i18nKey="active_processes.note_message"
                defaults="Some payloads that inject threads into system processes (like <1>SceShellCore</1>) will persist inside those processes even after their main process is killed."
                components={{ 1: <code className="bg-white/10 px-1 py-0.5 rounded text-ps-blue font-mono text-sm" /> }}
              />
            </p>
          </div>
        </div>
      </section>
    </div>
  )
}

export default ActiveProcessesView
