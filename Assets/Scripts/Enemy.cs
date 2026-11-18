using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

public class Enemy : MonoBehaviour
{
    [SerializeField] private NavMeshAgent agent;
    [SerializeField] private Transform player;
    [SerializeField] private GameObject bullet;
    [SerializeField] private Transform firePoint;
    [SerializeField] private Animator animator;

    [Header("Ranges")]

    [SerializeField] private float chaseRange = 15f;
    [SerializeField] private float attackRange = 10f;
    [SerializeField] private float timeBetweenAttacks = 1f;

    private float nextShootTime;
    private const string isChassing = "isChasing";
    private const string isAttacking = "isShooting";


    private void Update()
    {

        if (player == null) return;

        float distance = Vector3.Distance(transform.position, player.position);
        if (distance <= attackRange)
        {
            Attack();
        }
        else if (distance <= chaseRange)
        {
            Chase();
        }
        else
        {
            Idle();
        }
    }
    private void Idle()
    {
        agent.ResetPath();
        SetAnimationt(false, false);
    }
    private void Chase()
    {
        agent.SetDestination(player.position);
        SetAnimationt(true, false);
    }

    private void Attack()
    {
        agent.ResetPath();
        LookAtPlayer();
        SetAnimationt(false, true);

        if (Time.time >= nextShootTime)
        {
            Instantiate(bullet, firePoint.position, firePoint.rotation);
            nextShootTime = Time.time + timeBetweenAttacks;
        }
    }

    private void LookAtPlayer()
    {
        Vector3 target = player.position;
        target.y = transform.position.y;
        transform.LookAt(target);
    }

    private void SetAnimationt(bool chasing, bool shooting)
    {
        animator.SetBool(isChassing, chasing);
        animator.SetBool(isAttacking, shooting);
    }


    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, chaseRange);
    }


}
