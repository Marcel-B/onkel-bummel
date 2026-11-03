import { AxiosError } from 'axios'
import { defineStore } from 'pinia'
import { ref } from 'vue'
import { api, errorText, signIn } from '@/api/client'
import type { WebUser } from '@/api/types'

export type AuthState = 'loading' | 'signedIn' | 'forbidden' | 'error'

export const useAuthStore = defineStore('auth', () => {
  const state = ref<AuthState>('loading')
  const user = ref<WebUser | null>(null)
  const error = ref('')

  async function load() {
    state.value = 'loading'
    try {
      user.value = await api.me()
      state.value = 'signedIn'
    } catch (e) {
      const status = e instanceof AxiosError ? e.response?.status : undefined
      if (status === 401) {
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

  return { state, user, error, load }
})
