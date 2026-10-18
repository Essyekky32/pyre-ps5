import React, { useState, useEffect, useMemo, useLayoutEffect, useCallback } from 'react'
import { CloudDownload, Upload, Package, Database, RefreshCw, Loader2, AlertTriangle, HardDrive, Usb, ChevronDown, Globe, Search, ArrowUpDown, X } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { QRCodeSVG } from 'qrcode.react'
import { cn, isPS5, isIOS } from '../../utils/helpers'
import { getPayloadStatus, resolveSourceDisplayName, isRepositoryPayloadVisible } from '../../utils/payloadStatus'
import { slug } from '../../utils/versions'
import PayloadName from '../ui/PayloadName'
import Modal from '../ui/Modal'
import { useRepository } from '../../contexts/RepositoryContext'
import PayloadCard from '../ui/PayloadCard'
import PayloadDetailPanel from '../ui/PayloadDetailPanel'

const StorageHub = ({
  items = [],
  self = null,
  payloads = [],
  onInstall,
  onDelete,
  onUpload,
  onImportFromUsb,
  config,
  ip,
  scrollTarget,
  onClearScrollTarget,
  selectedPayload: selectedPayloadProp,
  onSelectPayload,
  onClosePayload,
  conflictPrompt: externalConflictPrompt,
  setConflictPrompt: externalSetConflictPrompt,
  externalLinkUrl,
  setExternalLinkUrl,
  installingPayloadId = null,
}) => {
  const { t } = useTranslation()
  const multiSources = config?.MULTI_SOURCES_ENABLED === true

  /* PYRE: the repository catalogue has been removed from this view, so only the
     parts of the repository context still used by the installed/USB sections are
     destructured here. `error` and `fetchRemote` went with the catalogue. */
  const { sources, repoData, loading } = useRepository()

  const [localConflictPrompt, setLocalConflictPrompt] = useState(null)
  const conflictPrompt = externalConflictPrompt !== undefined ? externalConflictPrompt : localConflictPrompt
  const setConflictPrompt = externalSetConflictPrompt || setLocalConflictPrompt

  const [localSelectedPayload, setLocalSelectedPayload] = useState(null)
  const selectedPayload = selectedPayloadProp !== undefined ? selectedPayloadProp : localSelectedPayload
  const setSelectedPayload = onSelectPayload || setLocalSelectedPayload

  const handleCloseDetail = useCallback(() => {
    if (onClosePayload) {
      onClosePayload()
    } else {
      setSelectedPayload(null)
    }
  }, [onClosePayload, setSelectedPayload])

  useEffect(() => {
    if (scrollTarget) {
      const timer = setTimeout(() => {
        const element = document.getElementById(scrollTarget)
        if (element) element.scrollIntoView({ behavior: 'smooth' })
        if (onClearScrollTarget) onClearScrollTarget()
      }, 300)
      return () => clearTimeout(timer)
    }
  }, [scrollTarget, onClearScrollTarget])

  /* ---- Source-grouped data with statusInfo ---- */
  const enrichedSources = useMemo(() => {
    return (sources || []).map((src) => {
      const rawPayloads = src.payloads || []
      const filteredPayloads = rawPayloads.filter((p) => isRepositoryPayloadVisible(p, src.url))
      const enrichedPayloads = filteredPayloads.map((p) => {
        const statusInfo = getPayloadStatus({
          payload: p,
          srcUrl: src.url,
          items,
          self,
          sources,
          multiSources,
        })
        return {
          ...p,
          statusInfo,
          isInstalled: statusInfo.isInstalled,
          isUpdate: statusInfo.isUpdate,
        }
      })

      return {
        ...src,
        payloads: enrichedPayloads,
      }
    })
  }, [sources, items, multiSources, self])

  // eslint-disable-next-line react-hooks/preserve-manual-memoization
  const resolvedPayloadData = useMemo(() => {
    if (!selectedPayload) return null

    const targetPayload =
      selectedPayload.payload ||
      (typeof selectedPayload === 'object' && (selectedPayload.name || selectedPayload.id || selectedPayload.filename)
        ? selectedPayload
        : null)

    const targetIdentifier =
      selectedPayload.payload?.id ||
      selectedPayload.id ||
      selectedPayload.payload?.filename ||
      selectedPayload.filename ||
      (typeof selectedPayload === 'string' ? selectedPayload : null)

    const targetSrcId = selectedPayload.srcId || null
    const targetSrcUrl = selectedPayload.srcUrl || null

    // Check candidate sources from enrichedSources first so that fresh status is always picked up
    const candidateSources = targetSrcId
      ? [
          enrichedSources.find((s) => s.id === targetSrcId),
          ...enrichedSources.filter((s) => s.id !== targetSrcId),
        ].filter(Boolean)
      : (targetSrcUrl
          ? [
              enrichedSources.find((s) => s.url === targetSrcUrl),
              ...enrichedSources.filter((s) => s.url !== targetSrcUrl),
            ].filter(Boolean)
          : enrichedSources)

    for (const src of candidateSources) {
      const match = src.payloads?.find(
        (candidate) =>
          (targetIdentifier && candidate.id === targetIdentifier) ||
          (targetIdentifier && candidate.filename === targetIdentifier) ||
          (targetIdentifier && candidate.filename?.toLowerCase() === targetIdentifier.toLowerCase()) ||
          (targetPayload && (candidate.id === targetPayload.id || candidate.filename === targetPayload.filename))
      )
      if (match) {
        return {
          payload: match,
          srcId: src.id,
          srcUrl: src.url,
          statusInfo: match.statusInfo,
          isInstalled: match.isInstalled,
          isUpdate: match.isUpdate,
        }
      }
    }

    if (targetPayload && (targetPayload.name || targetPayload.id || targetPayload.filename)) {
      const srcUrl =
        targetSrcUrl ||
        (targetSrcId ? enrichedSources.find((s) => s.id === targetSrcId)?.url : enrichedSources[0]?.url) ||
        ''
      const statusInfo = getPayloadStatus({
        payload: targetPayload,
        srcUrl,
        items,
        self,
        sources,
        multiSources,
      })
      return {
        ...selectedPayload,
        payload: targetPayload,
        srcId: targetSrcId || enrichedSources[0]?.id || '',
        srcUrl,
        statusInfo,
        isInstalled: statusInfo.isInstalled,
        isUpdate: statusInfo.isUpdate,
      }
    }

    return null
  }, [selectedPayload, enrichedSources, items, sources, multiSources, self])

  const currentDetailId =
    resolvedPayloadData?.payload?.id ||
    (resolvedPayloadData?.payload
      ? slug(resolvedPayloadData.payload.name || resolvedPayloadData.payload.filename)
      : null)
  const isDetailInstalling = Boolean(installingPayloadId && currentDetailId === installingPayloadId)

  useLayoutEffect(() => {
    const mainEl = document.querySelector('main')
    if (!mainEl) return
    /* PYRE: the repository catalogue is gone, so there is no list scroll
       position left to save and restore. Opening a detail panel still jumps
       to the top, which is what it did before. */
    if (resolvedPayloadData) mainEl.scrollTop = 0
  }, [resolvedPayloadData])

  useEffect(() => {
    if (selectedPayload && !resolvedPayloadData && !loading && repoData) {
      handleCloseDetail()
    }
  }, [selectedPayload, resolvedPayloadData, loading, repoData, handleCloseDetail])

  const handleInstallClick = (payload, srcId, srcUrl, statusInfo) => {
    // If there is an install conflict across repositories
    if (statusInfo?.hasConflict && statusInfo?.conflictSlot) {
      const srcObj = enrichedSources.find((s) => s.id === srcId || s.url === srcUrl)
      setConflictPrompt({
        payload,
        srcId,
        srcUrl,
        conflictSlot: statusInfo.conflictSlot,
        thisSourceName: srcObj?.name || 'this repository',
      })
      return
    }

    onInstall(payload, srcId, srcUrl)
  }

  if (selectedPayload && !resolvedPayloadData && (loading || !repoData)) {
    return (
      <div className="py-24 flex flex-col items-center justify-center space-y-4">
        <Loader2 className="w-10 h-10 text-ps-blue animate-spin" />
        <p className="text-zinc-400 font-medium">{t("storage_hub.loading", "Loading repository...")}</p>
      </div>
    )
  }

  if (resolvedPayloadData) {
    return (
      <div className="w-full min-w-0 max-w-full">
        <PayloadDetailPanel
          payload={resolvedPayloadData.payload}
          srcId={resolvedPayloadData.srcId}
          srcUrl={resolvedPayloadData.srcUrl}
          statusInfo={resolvedPayloadData.statusInfo}
          isInstalled={resolvedPayloadData.isInstalled}
          isUpdate={resolvedPayloadData.isUpdate}
          isInstalling={isDetailInstalling}
          onDelete={onDelete}
          externalLinkUrl={externalLinkUrl}
          setExternalLinkUrl={setExternalLinkUrl}
          onClose={handleCloseDetail}
          onInstall={(p, sid, surl, sInfo) => {
            handleInstallClick(p, sid, surl, sInfo || resolvedPayloadData.statusInfo)
          }}
        />

        {/* Conflict Modal */}
        <Modal
          show={Boolean(conflictPrompt)}
          title={t("storage_hub.conflict_title", "Payload Conflict")}
          onClose={() => setConflictPrompt(null)}
          footer={
            <div className="flex flex-col sm:flex-row w-full gap-3">
              <button
                type="button"
                onClick={() => setConflictPrompt(null)}
                className="flex-1 py-3 px-4 rounded-xl bg-white/5 hover:bg-white/10 text-white font-bold uppercase text-sm transition-all"
              >
                {t("common.cancel", "Cancel")}
              </button>
              <button
                type="button"
                onClick={() => {
                  const cp = conflictPrompt
                  setConflictPrompt(null)
                  onInstall(cp.payload, cp.srcId, cp.srcUrl, {
                    mode: 'replace',
                    replaceSlot: cp.conflictSlot.slot,
                  })
                }}
                className="flex-1 py-3 px-4 rounded-xl bg-amber-600 hover:bg-amber-500 text-white font-black uppercase text-sm transition-all shadow-lg"
              >
                {t("storage_hub.conflict_replace", "Replace")}
              </button>
              <button
                type="button"
                onClick={() => {
                  const cp = conflictPrompt
                  setConflictPrompt(null)
                  onInstall(cp.payload, cp.srcId, cp.srcUrl, {
                    mode: 'keep_both',
                  })
                }}
                className="flex-1 py-3 px-4 rounded-xl bg-ps-blue hover:bg-ps-blue/80 text-white font-black uppercase text-sm transition-all shadow-lg"
              >
                {t("storage_hub.conflict_keep_both", "Keep both")}
              </button>
            </div>
          }
        >
          <p>
            {conflictPrompt &&
              t(
                "storage_hub.conflict_message",
                "\"{{name}}\" is already installed from {{otherSource}} ({{version}}). Install from {{thisSource}} too?",
                {
                  name: conflictPrompt.payload.name || conflictPrompt.payload.id,
                  otherSource: resolveSourceDisplayName(conflictPrompt.conflictSlot, sources),
                  version: conflictPrompt.conflictSlot.version || "",
                  thisSource: conflictPrompt.thisSourceName,
                }
              )}
          </p>
        </Modal>
      </div>
    )
  }

  return (
    <div className="space-y-12 animate-in fade-in duration-300">
      {/* Conflict Modal */}
      <Modal
        show={Boolean(conflictPrompt)}
        title={t("storage_hub.conflict_title", "Payload Conflict")}
        onClose={() => setConflictPrompt(null)}
        footer={
          <div className="flex flex-col sm:flex-row w-full gap-3">
            <button
              type="button"
              onClick={() => setConflictPrompt(null)}
              className="flex-1 py-3 px-4 rounded-xl bg-white/5 hover:bg-white/10 text-white font-bold uppercase text-sm transition-all"
            >
              {t("common.cancel", "Cancel")}
            </button>
            <button
              type="button"
              onClick={() => {
                const cp = conflictPrompt
                setConflictPrompt(null)
                onInstall(cp.payload, cp.srcId, cp.srcUrl, {
                  mode: 'replace',
                  replaceSlot: cp.conflictSlot.slot,
                })
              }}
              className="flex-1 py-3 px-4 rounded-xl bg-amber-600 hover:bg-amber-500 text-white font-black uppercase text-sm transition-all shadow-lg"
            >
              {t("storage_hub.conflict_replace", "Replace")}
            </button>
            <button
              type="button"
              onClick={() => {
                const cp = conflictPrompt
                setConflictPrompt(null)
                onInstall(cp.payload, cp.srcId, cp.srcUrl, {
                  mode: 'keep_both',
                })
              }}
              className="flex-1 py-3 px-4 rounded-xl bg-ps-blue hover:bg-ps-blue/80 text-white font-black uppercase text-sm transition-all shadow-lg"
            >
              {t("storage_hub.conflict_keep_both", "Keep both")}
            </button>
          </div>
        }
      >
        <p>
          {conflictPrompt &&
            t(
              "storage_hub.conflict_message",
              "\"{{name}}\" is already installed from {{otherSource}} ({{version}}). Install from {{thisSource}} too?",
              {
                name: conflictPrompt.payload.name || conflictPrompt.payload.id,
                otherSource: resolveSourceDisplayName(conflictPrompt.conflictSlot, sources),
                version: conflictPrompt.conflictSlot.version || "",
                thisSource: conflictPrompt.thisSourceName,
              }
            )}
        </p>
      </Modal>

      <div className="flex items-center justify-between gap-4">
        <h2 className="text-3xl sm:text-4xl font-extrabold text-white tracking-tight shrink-0">
          {t("storage_hub.title_1", "Get")} <span className="text-ps-blue">{t("storage_hub.title_2", "Payloads")}</span>
        </h2>

        <div className="flex items-center space-x-2 sm:space-x-3">
          {!isPS5 && (
            <label
              className="p-3 rounded-full transition-colors flex items-center justify-center bg-white/5 text-zinc-400 hover:text-white hover:bg-white/10 cursor-pointer"
              title={t("storage_hub.upload_btn", "Upload ELF Payload")}
            >
              <Upload className="w-6 h-6" />
              <input type="file" className="hidden" onChange={onUpload} accept={isIOS ? undefined : ".elf,.bin"} />
            </label>
          )}
        </div>
      </div>

      {/* USB Storage Section */}
      <section id="usb-storage" className="space-y-6 -mx-3 sm:-mx-6 md:-mx-10 lg:-mx-11">
        <div className="flex items-center justify-between px-3 sm:px-4 md:px-6">
          <h3 className="label-caps !text-ps-blue flex items-center space-x-4 text-lg">
            <HardDrive className="w-6 h-6" />
            <span>{t("storage_hub.usb_storage_title", "USB Storage")}</span>
          </h3>
          <span className="bg-ps-blue/5 px-4 py-1 rounded-full text-ps-blue font-bold text-xs border border-ps-blue/20">
            {t("storage_hub.files_count", "{{count}} File", {
              count: payloads.filter((p) => p.includes('/mnt/usb')).length,
            })}
          </span>
        </div>

        <div className={cn("grid gap-4", isPS5 ? "grid-cols-2" : "grid-cols-1 lg:grid-cols-2")}>
          {payloads.filter((p) => p.includes('/mnt/usb')).length === 0 ? (
            <div className="col-span-full py-20 border-2 border-dashed border-white/5 rounded-ps-3xl flex flex-col items-center justify-center space-y-6 bg-white/[0.01]">
              <div className="relative">
                <HardDrive className="w-16 h-16 text-white/5" />
                {!config?.SCAN_USB_PAYLOADS && (
                  <div className="absolute -top-1 -right-1 bg-amber-500/20 rounded-full p-1 border border-amber-500/50">
                    <AlertTriangle className="w-4 h-4 text-amber-500" />
                  </div>
                )}
              </div>
              <div className="text-center space-y-2">
                <p className="text-zinc-500 font-bold uppercase tracking-widest text-sm italic">
                  {t("storage_hub.no_usb_payloads", "No USB Payloads Found")}
                </p>
                {!config?.SCAN_USB_PAYLOADS && (
                  <p className="text-xs text-zinc-600 max-w-xs mx-auto leading-relaxed">
                    {t("storage_hub.usb_scan_disabled_1", "Extended USB scanning is currently disabled.")} <br />
                    <span
                      dangerouslySetInnerHTML={{
                        __html: t(
                          "storage_hub.usb_scan_disabled_2",
                          "Enable <strong>\"Scan USB Payloads\"</strong> in settings to search for payloads on connected drives."
                        ),
                      }}
                    />
                  </p>
                )}
              </div>
            </div>
          ) : (
            payloads
              .filter((p) => p.includes('/mnt/usb'))
              .map((path) => {
                return (
                  <div
                    key={path}
                    className="group flex flex-row items-center justify-between p-4 md:p-6 glass-card rounded-ps-2xl border-white/10 hover:border-ps-blue/30 gap-4"
                  >
                    <div className="flex items-center space-x-4 md:space-x-6 min-w-0">
                      <div className="p-3 md:p-4 bg-white/5 rounded-2xl group-hover:bg-ps-blue/10 transition-colors shrink-0">
                        <Usb className="w-6 h-6 md:w-8 md:h-8 text-zinc-400 group-hover:text-ps-blue transition-colors" />
                      </div>
                      <div className="space-y-1 min-w-0 flex-1">
                        <PayloadName path={path} className="text-xl md:text-2xl text-white" stacked hideIcon={true} />
                        <p className="text-[10px] text-zinc-600 font-medium font-mono uppercase tracking-tighter opacity-60 truncate">
                          {path}
                        </p>
                      </div>
                    </div>
                    <div className="flex items-center shrink-0">
                      <button
                        onClick={() => onImportFromUsb(path)}
                        className="flex items-center space-x-2 md:space-x-3 px-4 md:px-6 py-3 md:py-4 bg-white/5 hover:bg-ps-blue text-white rounded-xl font-bold text-xs md:text-sm transition-all border border-white/10 hover:border-ps-blue group/btn"
                      >
                        <Database className="w-4 h-4 md:w-5 md:h-5 text-ps-blue group-hover/btn:text-white transition-colors" />
                        <span>{t("storage_hub.move_internal_btn", "Move to Internal")}</span>
                      </button>
                    </div>
                  </div>
                )
              })
          )}
        </div>
      </section>

      {/* Footer Info for PS5 */}
      {isPS5 && (
        <div
          className={cn(
            "glass-card p-6 md:p-10 rounded-ps-3xl flex items-center gap-8 md:gap-12 border-white/10 bg-black/40 mt-12 md:mt-16",
            isPS5 ? "flex-row" : "flex-col md:flex-row"
          )}
        >
          <div className="flex flex-col items-center space-y-4 md:space-y-6 shrink-0">
            <div className="bg-white p-4 md:p-6 rounded-3xl">
              <QRCodeSVG value={`http://${ip}:8084`} size={isPS5 ? 160 : 120} level="M" />
            </div>
            <code className="text-white font-mono text-base md:text-lg font-black opacity-90 italic tracking-tight uppercase">
              {ip}:8084
            </code>
          </div>
          <div className="flex-1 space-y-3 md:space-y-4 text-center md:text-left">
            <h4 className="label-caps !text-white !opacity-100 text-xl md:text-2xl tracking-widest flex items-center justify-center md:justify-start space-x-3 md:space-x-4">
              <div className="w-1.5 h-6 md:w-2 md:h-8 bg-ps-blue rounded-full" />
              <span>{t("storage_hub.remote_management_title", "Remote Management")}</span>
            </h4>
            <p className="text-lg md:text-xl text-zinc-400 leading-relaxed italic font-medium max-w-3xl">
              {t(
                "storage_hub.remote_management_desc",
                "Access this dashboard from your computer or phone to manage and upload payloads directly from your local network."
              )}
            </p>
          </div>
        </div>
      )}
    </div>
  )
}

export default StorageHub
