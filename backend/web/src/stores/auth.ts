import { AxiosError } from 'axios'
import { defineStore } from 'pinia'
import { ref } from 'vue'
import { api, errorText, signIn } from '@/api/client'
import type { WebUser } from '@/api/types'

export type AuthState = 'loading' | 'signedIn' | 'forbidden' | 'needsKey' | 'error'

export const useAuthStore = defineStore('auth', () => {
  const state = ref<AuthState>('loading')
  const user = ref<WebUser | null>(null)
  const error = ref('')
  /** `key` on a server without identity server: the web interface asks for the access key. */
  const login = ref<'oidc' | 'key'>('oidc')

  async function load() {
    state.value = 'loading'
    try {
      user.value = await api.me()
      state.value = 'signedIn'
    } catch (e) {
      const status = e instanceof AxiosError ? e.response?.status : undefined
      if (status === 401) {
        try {
          login.value = (await api.config()).login
        } catch (configError) {
          error.value = errorText(configError)
          state.value = 'error'
          return
        }
        if (login.value === 'key') {
          state.value = 'needsKey'
          return
        }
        // Not after a failed login, or it would go round in circles.
        if (new URLSearchParams(location.search).get('login') !== 'failed') return signIn()
        error.value = 'Die Anmeldung beim Identity-Server hat nicht geklappt.'
      } else if (status === 403) {
        state.value = 'forbidden'
        return
      } else {
        error.value = errorText(e)
      }
      state.value = 'error'
    }
  }

  /** Signs in with the access key; false with the reason in `error` when it did not work. */
  async function signInWithKey(key: string): Promise<boolean> {
    error.value = ''
    try {
      await api.signInWithKey(key.trim())
    } catch (e) {
      error.value = errorText(e)
      return false
    }
    await load()
    return state.value === 'signedIn'
  }

  return { state, user, error, login, load, signInWithKey }
})
