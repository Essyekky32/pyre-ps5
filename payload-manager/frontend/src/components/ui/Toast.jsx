import React, { useEffect, useRef } from 'react'
import { CheckCircle2, AlertTriangle } from 'lucide-react'
import { cn } from '../../utils/helpers'

const Toast = ({ message, type = 'success', onClose }) => {
  const closeRef = useRef(onClose)
  useEffect(() => {
    closeRef.current = onClose
  }, [onClose])
  useEffect(() => {
    const timer = setTimeout(() => closeRef.current(), 3000)
    return () => clearTimeout(timer)
  }, [])

  return (
    <div role={type === 'success' ? 'status' : 'alert'} className={cn(
      "relative shrink-0 flex items-center space-x-4 p-5 rounded-2xl border shadow-2xl animate-in slide-in-from-right duration-300 pointer-events-auto",
      type === 'success' ? "bg-emerald-950/90 border-emerald-500/50 text-emerald-400" : "bg-red-950/90 border-red-500/50 text-red-400"
    )}>
      {type === 'success' ? <CheckCircle2 className="w-6 h-6 shrink-0" /> : <AlertTriangle className="w-6 h-6 shrink-0" />}
      <span className="min-w-0 break-words font-bold uppercase tracking-tight">{message}</span>
    </div>
  )
}

export default Toast
