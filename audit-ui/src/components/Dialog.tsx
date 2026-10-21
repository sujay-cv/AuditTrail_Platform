import { useEffect, useRef, type ReactNode } from 'react'

/**
 * Accessible modal using the native <dialog> element: showModal() traps focus, Escape closes it,
 * and focus returns to the opener when it closes.
 */
export function Dialog({ open, title, onClose, children }: { open: boolean; title: string; onClose: () => void; children: ReactNode }) {
  const ref = useRef<HTMLDialogElement>(null)

  useEffect(() => {
    const dialog = ref.current
    if (!dialog) return
    if (open && !dialog.open) dialog.showModal?.()
    if (!open && dialog.open) dialog.close?.()
  }, [open])

  return (
    <dialog ref={ref} aria-labelledby="dialog-title" onClose={onClose} onCancel={onClose}>
      <h2 id="dialog-title">{title}</h2>
      {open && children}
    </dialog>
  )
}
