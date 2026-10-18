import React from 'react'
import { useTranslation } from 'react-i18next'
import { Loader2, Globe, Star, ChevronLeft, ChevronRight, CheckCircle2, Trash2 } from 'lucide-react'
import PayloadName from './PayloadName'
import PayloadIcon from './PayloadIcon'
import { cn, formatSourceTag } from '../../utils/helpers'

const PayloadButton = ({
  path = '',
  itemKey,
  onClick,
  onDelete,
  isLoading,
  sourceName,
  version,
  icon,
  author,
  displayName,
  isFavorite,
  isLaunched,
  isEditMode,
  isManageMode,
  onMoveFavorite,
  canMoveLeft,
  canMoveRight,
}) => {
  const { t } = useTranslation()
  const safePath = typeof path === 'string' ? path : ''
  const isUsb = Boolean(safePath && safePath.startsWith('/mnt/usb'))
  const iconName = safePath ? safePath.split('/').pop() : (displayName || 'Payload')

  const handleClick = (e) => {
    if (isManageMode) {
      if (!isUsb) {
        if (onDelete) onDelete()
        else if (onClick) onClick(e)
      }
    } else if (onClick) {
      onClick(e)
    }
  }

  const handleMove = (e, direction) => {
    e.stopPropagation()
    if (!onMoveFavorite) return
    if (onMoveFavorite.length === 1) {
      onMoveFavorite(direction)
    } else {
      onMoveFavorite(itemKey || safePath, direction)
    }
  }

  return (
    <div
      className={cn(
        "group glass-card rounded-ps-xl flex flex-col justify-center border transition-all text-left relative min-h-[72px]",
        isManageMode ? "border-red-500/20 hover:border-red-500 hover:bg-red-500/10" : "border-white/5 hover:border-ps-blue hover:bg-ps-blue/5",
        isManageMode && isUsb && "opacity-50 grayscale cursor-not-allowed"
      )}
    >
      <button
        onClick={handleClick}
        disabled={isLoading || (isManageMode && isUsb)}
        title={isManageMode && !isUsb ? t("app.dashboard.removePayload", "Remove Payload") : undefined}
        className="w-full flex-1 pl-4 pr-3 py-2.5 text-left relative z-10 rounded-ps-xl focus:outline-none focus:ring-2 focus:ring-ps-blue"
      >
      <div className="flex items-center justify-between w-full z-10 my-auto">
        <div className="flex items-center gap-3.5 min-w-0 pr-2">
          <PayloadIcon icon={icon} name={iconName} className="w-11 h-11 text-xl shrink-0" />
          <div className="min-w-0 flex-1 flex flex-col justify-center">
            <PayloadName path={safePath} version={version} hideVersion={true} displayName={displayName} className="text-white text-lg md:text-xl leading-snug" stacked={false} />
            <div className="flex items-center gap-2 mt-0.5">
              {version && <span className="text-[10px] text-zinc-400 font-bold bg-white/10 px-1.5 py-0.5 rounded uppercase shrink-0 leading-none">{version}</span>}
              {author && (
                <div className="text-[10px] text-zinc-500 font-medium truncate leading-none">
                  {author}
                </div>
              )}
            </div>
            {sourceName && !isUsb && (
              <div className="flex items-center mt-0.5">
                <span className="inline-flex items-center gap-1 text-[10px] text-zinc-400 font-bold bg-white/10 px-1.5 py-0.5 rounded uppercase shrink-0 max-w-full select-none leading-none" title={sourceName}>
                  <Globe className="w-3 h-3 text-zinc-400 shrink-0" />
                  <span className="truncate">{formatSourceTag(sourceName)}</span>
                </span>
              </div>
            )}
          </div>
        </div>
        <div className="flex items-center gap-2 shrink-0">
          {isEditMode && (
            <div
              className={cn(
                "p-1 rounded-lg transition-all",
                isFavorite ? "text-yellow-400 opacity-100" : "text-zinc-600 opacity-50"
              )}
            >
              <Star className={cn("w-5 h-5", isFavorite && "fill-yellow-400")} />
            </div>
          )}
          {isManageMode && !isUsb && (
            <div
              className="p-1.5 rounded-lg text-red-500 transition-all opacity-50 group-hover:opacity-100"
            >
              <Trash2 className="w-5 h-5" />
            </div>
          )}
          {isLaunched && !isEditMode && !isManageMode && !isLoading && (
            <div className="group/tooltip relative p-1 text-green-400">
              <CheckCircle2 className="w-5 h-5" />
              <div className="absolute bottom-full right-0 mb-2 px-2 py-1 bg-ps-black border border-white/10 shadow-lg text-xs font-bold text-white rounded opacity-0 group-hover/tooltip:opacity-100 transition-opacity pointer-events-none whitespace-nowrap z-50">
                {t("app.dashboard.launched_tooltip", "Launched recently")}
              </div>
            </div>
          )}
          {isLoading && <Loader2 className="w-6 h-6 animate-spin text-ps-blue" />}
        </div>
      </div>
      {isUsb && (
        <div className="mt-1.5 text-[10px] text-zinc-500 font-medium truncate opacity-60 group-hover:opacity-100 transition-opacity z-10 select-none">
          {safePath}
        </div>
      )}
      </button>
      {(isEditMode && isFavorite) && (
        <div className="absolute bottom-2 right-3 flex items-center gap-2 z-20">
          <button
            onClick={(e) => { if (canMoveLeft) handleMove(e, -1); }}
            aria-label={t("app.dashboard.move_left", "Move Left")}
            disabled={!canMoveLeft}
            className="p-1.5 bg-ps-black border border-white/10 rounded-lg hover:bg-ps-blue/20 hover:border-ps-blue hover:text-ps-blue transition-colors disabled:opacity-30 disabled:pointer-events-none text-zinc-400"
          >
            <ChevronLeft className="w-4 h-4" />
          </button>
          <button
            onClick={(e) => { if (canMoveRight) handleMove(e, 1); }}
            aria-label={t("app.dashboard.move_right", "Move Right")}
            disabled={!canMoveRight}
            className="p-1.5 bg-ps-black border border-white/10 rounded-lg hover:bg-ps-blue/20 hover:border-ps-blue hover:text-ps-blue transition-colors disabled:opacity-30 disabled:pointer-events-none text-zinc-400"
          >
            <ChevronRight className="w-4 h-4" />
          </button>
        </div>
      )}
      <div className="absolute inset-0 rounded-ps-xl bg-ps-blue/0 group-hover:bg-ps-blue/5 transition-colors z-0 pointer-events-none" />
    </div>
  )
}

export default PayloadButton
