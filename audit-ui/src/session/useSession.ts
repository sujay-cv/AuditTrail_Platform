import { useSyncExternalStore } from 'react'
import { getSession, setSession, subscribe, type Session } from './session'

export function useSession(): [Session, (s: Session) => void] {
  const session = useSyncExternalStore(subscribe, getSession)
  return [session, setSession]
}

export function useIsAdmin() {
  return useSession()[0].role === 'ComplianceAdmin'
}
